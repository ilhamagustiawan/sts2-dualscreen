// CloudSync — Steam Cloud saves for Slay the Spire 2.
//
// Adapted from SilksongAndroid (MIT, https://github.com/jakobkhansen/SilksongAndroid)
// src/SilksongLauncher.Launcher/.../CloudSync.kt. The transfer machinery
// (analyse → confirm → parallel transfer, timestamp comparison, post-push
// mtime realignment) is Silksong's; the path mapping is STS2's.
//
// STS2 writes its saves under an account root; the compat pack points that
// root at <files>/default/1 on Android (GamePrep.saveRoot):
//
//   profile.save
//   profile1/saves/progress.save, prefs.save, current_run.save, current_run_mp.save
//   profile1/saves/history/*.run
//   profile2/…, profile3/…, modded/profile1/…
//
// The game's Steam store canonicalises remote names by dropping "user://"
// and normalising slashes, so a cloud name is that same relative path,
// possibly under a prefix. Sts2SavePaths finds the relative part; anything it
// does not recognise is left alone, and settings.save is never synced
// (Android-only keys live in it, and PC settings should not follow you here).

package dev.sts2ds.launcher

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.File
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

object CloudSync {

    const val APP_ID = DepotFetcher.APP_ID

    // Cloud transfers are latency-bound: every file is its own
    // begin/commit RPC pair plus a CDN round-trip, and Steam has no
    // multi-file transfer API. Running several at once is the only way
    // to cut the wall-clock cost. Bounded to stay well under Steam's
    // per-file TooManyPending lease and any global pending cap — 4
    // captures most of the win at low risk. Tune here if needed.
    private const val MAX_PARALLEL_TRANSFERS = 4

    /** Used when the cloud has no saves to copy a prefix from. */
    private const val CLOUD_PREFIX_FALLBACK = ""

    /** Phases emitted by [pullItems] and [pushItems]. */
    sealed class Event {
        data class FileStarted(val filename: String, val index: Int, val total: Int) : Event()
        data class FileDone(val filename: String, val bytesWritten: Long) : Event()
        data class FileFailed(val filename: String, val error: String) : Event()
        data class Complete(val ok: Int, val failed: Int, val totalBytes: Long) : Event()
    }

    /**
     * A single local-target save to download from the cloud, plus the
     * matching cloud metadata so the caller can show progress info or
     * decide which subset to apply.
     */
    data class PullItem(
        val cloudFile: SteamCloudClient.CloudFile,
        val localFile: File,
        val cloudTimestampUnix: Long,
        val localMtimeUnix: Long,
    )

    /**
     * Result of [analyzePull]. Symmetric to [PushAnalysis]:
     *   - [toDownload]: cloud files that are strictly newer than the
     *     local copy (or that have no local counterpart yet). Safe to
     *     fetch without prompting.
     *   - [conflicts]: cloud files where LOCAL is newer than the cloud
     *     copy. A safe auto-pull skips these (so we don't clobber a
     *     fresh local save with an older cloud version); manual / UI
     *     callers can present them as overrides if they want.
     *   - [skipped]: cloud files whose timestamp matches local
     *     exactly. No-op — would only burn bandwidth.
     */
    data class PullAnalysis(
        val toDownload: List<PullItem>,
        val conflicts: List<PullItem>,
        val skipped: List<String>,
    ) {
        val hasConflicts: Boolean get() = conflicts.isNotEmpty()
    }

    /**
     * Mirror of [analyzePush] for the pull side. Logs in, enumerates,
     * compares each cloud file's timestamp against the matching local
     * file's mtime, and buckets them by direction:
     *
     *   cloud > local (or no local file) → toDownload
     *   local > cloud                    → conflicts
     *   timestamps equal                 → skipped
     *
     * Used by auto-pull, which only ever downloads [PullAnalysis.toDownload]
     * — the conflicts list is for diagnostics only there. Manual pull
     * does NOT use this analyser (it still clobbers local with the
     * full cloud set via [pullAll]); that matches what the user
     * explicitly asked for when they originally wired the manual Pull
     * button.
     */
    suspend fun analyzePull(
        context: Context,
        credentials: TokenStore.Credentials,
    ): PullAnalysis = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val saveDir = saveDirFor(context)
        saveDir.mkdirs()

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)
            LauncherLog.log("Pull analyze: enumerating cloud files…")
            val allCloud = cloud.enumerateFiles(APP_ID)
            val cloudFiles = allCloud.filter { Sts2SavePaths.relative(it.filename) != null }
            if (cloudFiles.size != allCloud.size) {
                LauncherLog.log("Pull analyze: ignoring ${allCloud.size - cloudFiles.size} cloud entr(ies) that are not saves")
            }

            val toDownload = mutableListOf<PullItem>()
            val conflicts = mutableListOf<PullItem>()
            val skipped = mutableListOf<String>()

            for (cf in cloudFiles) {
                val basename = Sts2SavePaths.relative(cf.filename)!!
                val local = File(saveDir, basename)
                val localMtime = if (local.isFile) local.lastModified() / 1000L else -1L
                val item = PullItem(
                    cloudFile = cf,
                    localFile = local,
                    cloudTimestampUnix = cf.timestampUnix,
                    localMtimeUnix = localMtime,
                )
                when {
                    localMtime < 0 -> {
                        toDownload.add(item)
                        LauncherLog.log(
                            "  to-download (new): $basename cloud=${cf.timestampUnix} size=${cf.size}"
                        )
                    }
                    cf.timestampUnix > localMtime -> {
                        toDownload.add(item)
                        LauncherLog.log(
                            "  to-download (cloud-newer): $basename local=$localMtime cloud=${cf.timestampUnix} delta=${cf.timestampUnix - localMtime}s sizes=${local.length()}↔${cf.size}"
                        )
                    }
                    cf.timestampUnix == localMtime -> skipped.add(basename)
                    else -> {
                        conflicts.add(item)
                        LauncherLog.log(
                            "  local-newer: $basename local=$localMtime cloud=${cf.timestampUnix} delta=${localMtime - cf.timestampUnix}s"
                        )
                    }
                }
            }

            LauncherLog.log(
                "Pull analyze: ${toDownload.size} to download, ${conflicts.size} local-newer (skipped), " +
                    "${skipped.size} unchanged"
            )
            PullAnalysis(toDownload, conflicts, skipped)
        }
    }

    /**
     * Downloads [items] in order, writing each into the local save
     * directory. Caller is expected to feed this the [PullAnalysis.toDownload]
     * list (potentially augmented with [PullAnalysis.conflicts] for a
     * "force overwrite" path, though no current caller does that).
     * Opens its own session — same rationale as [pushItems].
     */
    fun pullItems(
        context: Context,
        credentials: TokenStore.Credentials,
        items: List<PullItem>,
    ): Flow<Event> = channelFlow {
        if (items.isEmpty()) {
            send(Event.Complete(0, 0, 0))
            return@channelFlow
        }
        val saveDir = saveDirFor(context)
        saveDir.mkdirs()

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)

            val gate = Semaphore(MAX_PARALLEL_TRANSFERS)
            val ok = AtomicInteger(0)
            val failed = AtomicInteger(0)
            val totalBytes = AtomicLong(0L)
            val started = AtomicInteger(0)

            // Fan out downloads, at most MAX_PARALLEL_TRANSFERS in flight.
            // Each file is independent and its failure is caught per-item,
            // so one bad download never cancels its siblings. channelFlow
            // makes send() safe to call from these child coroutines.
            items.map { item ->
                launch(Dispatchers.IO) {
                    gate.withPermit {
                        send(Event.FileStarted(item.cloudFile.filename, started.incrementAndGet(), items.size))
                        try {
                            val bytes = cloud.downloadFile(APP_ID, item.cloudFile.filename)
                            item.localFile.parentFile?.mkdirs()
                            item.localFile.writeBytes(bytes)
                            // Match the cloud timestamp on disk so a subsequent
                            // analyzePull recognises this file as in-sync rather
                            // than re-downloading on the next start.
                            item.localFile.setLastModified(item.cloudTimestampUnix * 1000L)
                            totalBytes.addAndGet(bytes.size.toLong())
                            ok.incrementAndGet()
                            LauncherLog.log("  ↓ ${item.localFile.name}: ${bytes.size} bytes")
                            send(Event.FileDone(item.localFile.name, bytes.size.toLong()))
                        } catch (t: Throwable) {
                            failed.incrementAndGet()
                            val msg = t.message ?: t.javaClass.simpleName
                            LauncherLog.log("  ↓ ${item.localFile.name}: FAILED — $msg")
                            android.util.Log.e("Sts2Launcher.Cloud", "pull failed for ${item.cloudFile.filename}", t)
                            send(Event.FileFailed(item.localFile.name, msg))
                        }
                    }
                }
            }.joinAll()

            LauncherLog.log("Cloud pull (safe) done: ${ok.get()} ok, ${failed.get()} failed, ${totalBytes.get()} bytes")
            send(Event.Complete(ok.get(), failed.get(), totalBytes.get()))
        }
    }.flowOn(Dispatchers.IO)

    /**
     * A single local save that's a candidate for upload, paired with
     * the cloud destination path we'll write to. [cloudTimestampUnix]
     * is -1 if the cloud has no counterpart yet (i.e. a new file).
     */
    data class PushItem(
        val localFile: File,
        val cloudPath: String,
        val localMtimeUnix: Long,
        val cloudTimestampUnix: Long,
    )

    /**
     * Result of [analyzePush]:
     *   - [toUpload]: files where local mtime ≥ cloud (or cloud doesn't
     *     have it). These are safe to push without asking.
     *   - [conflicts]: files where cloud is NEWER than local. Pushing
     *     these would clobber a remote write the user hasn't seen. UI
     *     should prompt before including them.
     *   - [skipped]: local files where cloud already has the exact same
     *     timestamp (down to the second). Not uploaded — push would
     *     just churn the server-side timestamp for no gain.
     */
    data class PushAnalysis(
        val toUpload: List<PushItem>,
        val conflicts: List<PushItem>,
        val skipped: List<String>,
        /**
         * Cloud entries with no matching local file that must go: a
         * run save the game deleted when the run ended. Left on Steam
         * Cloud, the next pull would bring the finished run back.
         *
         * pushItems deletes these on the cloud side AFTER any
         * uploads succeed. Two guards keep it safe:
         *   1. analyzePush returns an EMPTY list here when no local
         *      files were visible at all (external storage
         *      unavailable, save dir missing…), so a transient read
         *      failure can never wipe the whole cloud save set.
         *   2. pushFlow runs a push even when this is the only
         *      non-empty bucket (nothing to upload), so a delete-only
         *      push still prunes orphans instead of skipping them.
         */
        val toDelete: List<SteamCloudClient.CloudFile>,
    ) {
        val hasConflicts: Boolean get() = conflicts.isNotEmpty()
        val all: List<PushItem> get() = toUpload + conflicts
    }

    /**
     * Logs in, enumerates the cloud, walks the local save directory and
     * partitions every `user*.dat` (and its sidecars) into upload /
     * conflict / skip buckets. Returns synchronously; no Flow, since
     * the UI normally wants the full picture before deciding whether
     * to show a confirmation dialog. Throws on log-on failure or
     * Steam-side errors.
     */
    suspend fun analyzePush(
        context: Context,
        credentials: TokenStore.Credentials,
    ): PushAnalysis = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val saveDir = saveDirFor(context)
        val locals = collectLocalSaveFiles(saveDir)
        LauncherLog.log("Push analyze: found ${locals.size} local save file(s) in ${saveDir.absolutePath}")

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)
            LauncherLog.log("Push analyze: enumerating cloud files…")
            val allCloud = cloud.enumerateFiles(APP_ID)
            val cloudFiles = allCloud.filter { Sts2SavePaths.relative(it.filename) != null }
            val cloudPrefix = cloudFiles.firstNotNullOfOrNull { Sts2SavePaths.prefix(it.filename) }
                ?: CLOUD_PREFIX_FALLBACK

            // Keyed by the save's path relative to the account root.
            val cloudByBasename = cloudFiles.associateBy { Sts2SavePaths.relative(it.filename)!! }

            val toUpload = mutableListOf<PushItem>()
            val conflicts = mutableListOf<PushItem>()
            val skipped = mutableListOf<String>()

            val localBasenames = locals.map { relativeTo(saveDir, it) }.toSet()
            // Cloud saves the device no longer has. Only a finished or
            // abandoned run is pruned: the game deletes current_run*.save
            // when a run ends, and leaving the cloud copy would bring the
            // run back on the next pull. Nothing else is ever deleted, and
            // nothing at all when no local saves were visible (storage
            // unavailable must never look like "everything was deleted").
            val toDelete = if (locals.isEmpty()) {
                emptyList()
            } else {
                cloudFiles.filter {
                    val rel = Sts2SavePaths.relative(it.filename)!!
                    rel !in localBasenames && Sts2SavePaths.isRunSave(rel)
                }
            }

            for (local in locals) {
                val basename = relativeTo(saveDir, local)
                val mtimeSec = local.lastModified() / 1000L
                val cloud = cloudByBasename[basename]
                if (cloud == null) {
                    // New file — push it.
                    toUpload.add(
                        PushItem(
                            localFile = local,
                            cloudPath = cloudPrefix + basename,
                            localMtimeUnix = mtimeSec,
                            cloudTimestampUnix = -1,
                        )
                    )
                    continue
                }
                val item = PushItem(
                    localFile = local,
                    cloudPath = cloud.filename,
                    localMtimeUnix = mtimeSec,
                    cloudTimestampUnix = cloud.timestampUnix,
                )
                when {
                    mtimeSec == cloud.timestampUnix -> skipped.add(basename)
                    mtimeSec > cloud.timestampUnix -> toUpload.add(item)
                    else -> conflicts.add(item)
                }
            }

            LauncherLog.log(
                "Push analyze: ${toUpload.size} to upload, ${conflicts.size} conflict(s), " +
                    "${skipped.size} unchanged, ${toDelete.size} cloud orphan(s) to delete"
            )
            PushAnalysis(toUpload, conflicts, skipped, toDelete)
        }
    }

    /**
     * Performs the actual upload of [items] (which the caller built
     * from [PushAnalysis], optionally including conflicts the user
     * confirmed). Opens its own session — analyze + push are
     * independent operations; the small extra login cost is worth not
     * having to thread a SteamSession across UI boundaries.
     */
    fun pushItems(
        credentials: TokenStore.Credentials,
        items: List<PushItem>,
        toDelete: List<SteamCloudClient.CloudFile> = emptyList(),
    ): Flow<Event> = channelFlow {
        if (items.isEmpty() && toDelete.isEmpty()) {
            send(Event.Complete(0, 0, 0))
            return@channelFlow
        }

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)

            val gate = Semaphore(MAX_PARALLEL_TRANSFERS)
            val ok = AtomicInteger(0)
            val failed = AtomicInteger(0)
            val totalBytes = AtomicLong(0L)
            val started = AtomicInteger(0)
            val succeeded = ConcurrentLinkedQueue<PushItem>()

            // Fan out uploads, at most MAX_PARALLEL_TRANSFERS in flight.
            // Per-file errors are caught so one failure never cancels the
            // rest; TooManyPending is a per-file lease, so distinct files
            // uploading concurrently are safe. channelFlow makes send()
            // safe to call from these child coroutines.
            items.map { item ->
                launch(Dispatchers.IO) {
                    gate.withPermit {
                        send(Event.FileStarted(item.localFile.name, started.incrementAndGet(), items.size))
                        try {
                            val content = item.localFile.readBytes()
                            cloud.uploadFile(
                                appId = APP_ID,
                                cloudPath = item.cloudPath,
                                rawContent = content,
                                timestampUnix = item.localMtimeUnix,
                            )
                            totalBytes.addAndGet(content.size.toLong())
                            ok.incrementAndGet()
                            succeeded.add(item)
                            LauncherLog.log(
                                "  ↑ ${item.localFile.name}: ${content.size} bytes -> ${item.cloudPath} (ts=${item.localMtimeUnix})"
                            )
                            send(Event.FileDone(item.localFile.name, content.size.toLong()))
                        } catch (t: Throwable) {
                            failed.incrementAndGet()
                            val msg = t.message ?: t.javaClass.simpleName
                            LauncherLog.log("  ↑ ${item.localFile.name}: FAILED — $msg")
                            android.util.Log.e("Sts2Launcher.Cloud", "push failed for ${item.cloudPath}", t)
                            send(Event.FileFailed(item.localFile.name, msg))
                        }
                    }
                }
            }.joinAll()

            // Drop cloud-side orphans (files local no longer has).
            // Done AFTER all uploads finish so a partial-failure scenario
            // doesn't lose data — we only ever remove from cloud what we
            // KNOW exists somewhere as the desktop copy already (the user
            // pulled it once and the game then expired it). Failures here
            // are non-fatal; they'll be retried next push. Parallelised
            // with the same bound.
            toDelete.map { orphan ->
                launch(Dispatchers.IO) {
                    gate.withPermit {
                        try {
                            cloud.deleteFile(APP_ID, orphan.filename)
                            LauncherLog.log("  ✗ deleted cloud orphan: ${orphan.filename}")
                        } catch (t: Throwable) {
                            val msg = t.message ?: t.javaClass.simpleName
                            LauncherLog.log("  ✗ delete failed for ${orphan.filename}: $msg")
                            android.util.Log.e("Sts2Launcher.Cloud", "delete failed for ${orphan.filename}", t)
                        }
                    }
                }
            }.joinAll()

            // Re-align local mtimes with whatever Steam ended up
            // storing for each file. Steam dedupes uploads by SHA-1
            // of the raw bytes: if the file we just uploaded has
            // the same content as the existing cloud copy, the
            // upload succeeds but the cloud's timestamp is NOT
            // updated — and we'd then perpetually re-detect
            // "local-newer" for that file (the game touches mtime
            // even when the bytes don't change — common for
            // `shared.dat`, which gets rewritten with identical 305
            // bytes on every launch). One re-enumeration after the
            // push lets us snap local mtime to cloud's authoritative
            // timestamp for each succeeded file, breaking the loop.
            if (succeeded.isNotEmpty()) {
                try {
                    val after = cloud.enumerateFiles(APP_ID).associateBy { it.filename }
                    for (item in succeeded) {
                        val cur = after[item.cloudPath] ?: continue
                        item.localFile.setLastModified(cur.timestampUnix * 1000L)
                    }
                } catch (t: Throwable) {
                    LauncherLog.log("  (post-push mtime sync skipped: ${t.message ?: t.javaClass.simpleName})")
                }
            }

            LauncherLog.log("Cloud push done: ${ok.get()} ok, ${failed.get()} failed, ${totalBytes.get()} bytes, ${toDelete.size} deleted")
            send(Event.Complete(ok.get(), failed.get(), totalBytes.get()))
        }
    }.flowOn(Dispatchers.IO)

    private fun saveDirFor(context: Context): File = GamePrep.saveRoot(context)

    /** Every file under the save root that is a save Sts2SavePaths syncs. */
    private fun collectLocalSaveFiles(saveDir: File): List<File> {
        if (!saveDir.isDirectory) return emptyList()
        return saveDir.walkTopDown()
            .filter { it.isFile && !it.name.startsWith(".") }
            .filter { Sts2SavePaths.isSave(relativeTo(saveDir, it)) }
            .sortedBy { relativeTo(saveDir, it) }
            .toList()
    }

    private fun relativeTo(root: File, file: File): String =
        file.relativeTo(root).invariantSeparatorsPath
}

/** Which files are STS2 saves, and where a cloud name's save path starts. */
object Sts2SavePaths {
    private val PROFILE = Regex("""(?:^|/)((?:modded/)?profile[1-9]/saves/.+)$""")
    private val PROFILE_SELECTOR = Regex("""(?:^|/)((?:modded/)?profile\.save)$""")

    private fun normalise(raw: String) = raw.removePrefix("user://").replace('\\', '/')

    /** The save path relative to the account root, or null if [cloudName] is not a save. */
    fun relative(cloudName: String): String? {
        val name = normalise(cloudName)
        val match = PROFILE.find(name) ?: PROFILE_SELECTOR.find(name) ?: return null
        return match.groupValues[1].takeIf(::isSave)
    }

    /** Everything in [cloudName] before its save path, to reuse for new uploads. */
    fun prefix(cloudName: String): String? {
        val name = normalise(cloudName)
        val rel = relative(cloudName) ?: return null
        return name.substring(0, name.length - rel.length)
    }

    fun isSave(relative: String): Boolean {
        if (relative.endsWith(".tmp") || relative.contains("/.")) return false
        return relative == "profile.save" || relative == "modded/profile.save" ||
            PROFILE.matches(relative) && (relative.endsWith(".save") || relative.endsWith(".backup") || relative.endsWith(".run"))
    }

    fun isRunSave(relative: String): Boolean =
        relative.substringAfterLast('/').startsWith("current_run")
}
