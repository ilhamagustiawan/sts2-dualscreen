// SaveSync — when to move saves, and which way.
//
// The flows are SilksongAndroid's pull/push flows (MIT), moved out of
// LauncherActivity so that the launcher, the pre-launch pull and the push
// after a game session all go through one place: analyse, decide, ask only
// if both sides moved, then transfer everything one way. The prompt itself
// is the caller's ([resolve]); this class never touches a view.
//
// Every run ends in an [Outcome] and leaves a record in SettingsStore (last
// sync time, last error, whether this device still has saves to upload), which
// is what the launcher's cloud status line reads.

package dev.sts2ds.launcher

import android.content.Context
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.Flow

/**
 * Which way to sync, from how many files are newer on each side.
 *
 * Both sides newer is the only case worth asking about: one side newer means
 * the other side simply has not caught up, and moving it is what the user
 * wants. Either way the transfer is the whole set one way, never a per-slot mix.
 */
object SyncPlan {
    enum class Action { NOTHING, DOWNLOAD, UPLOAD, ASK }

    /**
     * [deviceHasUnsyncedPlay] is true when a game session on this device has
     * not been uploaded yet. That counts as a device-side change even when no
     * mtime shows it: a finished run deletes its save, and a deletion has no
     * timestamp to compare.
     */
    fun decide(newerOnCloud: Int, newerOnDevice: Int, deviceHasUnsyncedPlay: Boolean = false): Action {
        val deviceChanged = newerOnDevice > 0 || deviceHasUnsyncedPlay
        return when {
            newerOnCloud > 0 && deviceChanged -> Action.ASK
            newerOnCloud > 0 -> Action.DOWNLOAD
            newerOnDevice > 0 -> Action.UPLOAD
            else -> Action.NOTHING
        }
    }
}

class SaveSync(
    private val context: Context,
    private val settings: SettingsStore,
    private val resolve: suspend (Conflict) -> Choice,
) {

    enum class Outcome { DONE, NOTHING, SKIPPED, CANCELLED, OFFLINE, AUTH_EXPIRED, FAILED }

    /**
     * KEEP_DEVICE / KEEP_CLOUD replace the other side's whole save set.
     * SKIP leaves both alone and lets a launch carry on; CANCEL stops it.
     */
    enum class Choice { KEEP_DEVICE, KEEP_CLOUD, SKIP, CANCEL }

    /** What the conflict prompt shows. Times are unix seconds, -1 if unknown. */
    data class Conflict(
        val files: List<String>,
        val deviceNewest: Long,
        val cloudNewest: Long,
        val beforeLaunch: Boolean,
    )

    /** Download newer cloud saves. [beforeLaunch] offers "play without syncing" on a conflict. */
    suspend fun pull(c: TokenStore.Credentials, beforeLaunch: Boolean = false): Outcome =
        guarded(if (beforeLaunch) "pre-launch pull" else "pull") { pullFlow(c, beforeLaunch) }

    /** Upload this device's newer saves; after a game session, or from the button. */
    suspend fun push(c: TokenStore.Credentials): Outcome = guarded("push") { pushFlow(c) }

    /** Both ways: cloud changes first, so the upload never sends an older set over a newer one. */
    suspend fun syncNow(c: TokenStore.Credentials): Outcome = guarded("sync") {
        when (val pulled = pullFlow(c, beforeLaunch = false)) {
            Outcome.CANCELLED -> pulled
            else -> pushFlow(c).let { if (it == Outcome.NOTHING) pulled else it }
        }
    }

    /**
     * Shared analyse + decide + transfer for a pull. Either pulls everything
     * the analyser flagged or pulls nothing.
     */
    private suspend fun pullFlow(c: TokenStore.Credentials, beforeLaunch: Boolean): Outcome {
        LauncherLog.log("Analyzing cloud vs local saves…")
        val analysis = CloudSync.analyzePull(context, c)
        val action = SyncPlan.decide(
            newerOnCloud = analysis.toDownload.size,
            newerOnDevice = analysis.conflicts.size,
            deviceHasUnsyncedPlay = settings.pendingPush,
        )
        return when (action) {
            SyncPlan.Action.NOTHING -> {
                LauncherLog.log("Pull: nothing newer on cloud")
                Outcome.NOTHING
            }
            SyncPlan.Action.UPLOAD -> {
                // Only this device is ahead: nothing to pull, and the upload
                // happens after the next session (or Sync now).
                LauncherLog.log("Pull: ${analysis.conflicts.size} save(s) newer on this device; upload pending")
                settings.pendingPush = true
                Outcome.NOTHING
            }
            SyncPlan.Action.DOWNLOAD -> {
                transfer(CloudSync.pullItems(context, c, analysis.toDownload))
                Outcome.DONE
            }
            SyncPlan.Action.ASK -> {
                val items = analysis.toDownload + analysis.conflicts
                val conflict = Conflict(
                    files = items.map { relative(it.localFile) },
                    deviceNewest = items.maxOfOrNull { it.localMtimeUnix } ?: -1L,
                    cloudNewest = items.maxOfOrNull { it.cloudTimestampUnix } ?: -1L,
                    beforeLaunch = beforeLaunch,
                )
                when (resolve(conflict)) {
                    Choice.KEEP_DEVICE -> {
                        LauncherLog.log("Conflict: keep device — pushing local saves over cloud")
                        pushLocalOverCloud(c)
                    }
                    Choice.KEEP_CLOUD -> {
                        LauncherLog.log("Conflict: keep cloud — overwriting local with cloud")
                        transfer(CloudSync.pullItems(context, c, items))
                        settings.pendingPush = false
                        Outcome.DONE
                    }
                    Choice.SKIP -> {
                        LauncherLog.log("Conflict: left as is, playing without syncing")
                        Outcome.SKIPPED
                    }
                    Choice.CANCEL -> {
                        LauncherLog.log("Pull cancelled by user")
                        Outcome.CANCELLED
                    }
                }
            }
        }
    }

    private suspend fun pushFlow(c: TokenStore.Credentials): Outcome {
        LauncherLog.log("Analyzing local vs cloud saves…")
        val analysis = CloudSync.analyzePush(context, c)
        // Cloud orphans to prune count as device-side changes: without them a
        // "delete-only" push (a run that ended here, whose save the cloud
        // still holds) would be skipped and the finished run would come back.
        val action = SyncPlan.decide(
            newerOnCloud = analysis.conflicts.size,
            newerOnDevice = analysis.toUpload.size + analysis.toDelete.size,
        )
        val outcome = when (action) {
            SyncPlan.Action.NOTHING -> {
                LauncherLog.log("Push: nothing to do (all ${analysis.skipped.size} local file(s) already match cloud, no orphans)")
                Outcome.NOTHING
            }
            SyncPlan.Action.DOWNLOAD -> {
                // Only the cloud is ahead: uploading would clobber it, and the
                // next pull brings it down.
                LauncherLog.log("Push: ${analysis.conflicts.size} save(s) newer on cloud; nothing to upload")
                Outcome.NOTHING
            }
            SyncPlan.Action.UPLOAD -> {
                transfer(CloudSync.pushItems(c, analysis.toUpload, toDelete = analysis.toDelete))
                Outcome.DONE
            }
            SyncPlan.Action.ASK -> {
                val items = analysis.all
                val conflict = Conflict(
                    files = items.map { relative(it.localFile) },
                    deviceNewest = items.maxOfOrNull { it.localMtimeUnix } ?: -1L,
                    cloudNewest = items.maxOfOrNull { it.cloudTimestampUnix } ?: -1L,
                    beforeLaunch = false,
                )
                when (resolve(conflict)) {
                    Choice.KEEP_DEVICE -> {
                        LauncherLog.log("Conflict: keep device — overwriting cloud with local")
                        transfer(CloudSync.pushItems(c, items, toDelete = analysis.toDelete))
                        Outcome.DONE
                    }
                    Choice.KEEP_CLOUD -> {
                        LauncherLog.log("Conflict: keep cloud — pulling cloud saves over local")
                        pullCloudOverLocal(c)
                    }
                    Choice.SKIP, Choice.CANCEL -> {
                        LauncherLog.log("Push cancelled by user")
                        return Outcome.CANCELLED
                    }
                }
            }
        }
        settings.pendingPush = false
        return outcome
    }

    /**
     * Forced full pull (no prompt): the user chose "keep cloud". Re-analyses
     * from scratch because the originating flow may have been a push.
     */
    private suspend fun pullCloudOverLocal(c: TokenStore.Credentials): Outcome {
        val analysis = CloudSync.analyzePull(context, c)
        val items = analysis.toDownload + analysis.conflicts
        if (items.isEmpty()) {
            LauncherLog.log("Keep cloud: nothing on cloud to pull")
            return Outcome.NOTHING
        }
        transfer(CloudSync.pullItems(context, c, items))
        return Outcome.DONE
    }

    /**
     * Forced full push (no prompt): the user chose "keep device". Re-analyses
     * from scratch because the originating flow may have been a pull.
     */
    private suspend fun pushLocalOverCloud(c: TokenStore.Credentials): Outcome {
        val analysis = CloudSync.analyzePush(context, c)
        if (analysis.all.isEmpty() && analysis.toDelete.isEmpty()) {
            LauncherLog.log("Keep device: nothing local to push")
            return Outcome.NOTHING
        }
        transfer(CloudSync.pushItems(c, analysis.all, toDelete = analysis.toDelete))
        settings.pendingPush = false
        return Outcome.DONE
    }

    /** Runs a transfer to the end; any file that did not make it fails the sync. */
    private suspend fun transfer(flow: Flow<CloudSync.Event>) {
        var failed = 0
        flow.collect { if (it is CloudSync.Event.Complete) failed = it.failed }
        if (failed > 0) throw RuntimeException("$failed file(s) did not transfer")
    }

    /** Offline check, error mapping and the status record around one sync. */
    private suspend fun guarded(what: String, block: suspend () -> Outcome): Outcome {
        if (!Network.isOnline(context)) {
            LauncherLog.log("Cloud $what skipped: offline")
            settings.lastSyncError = OFFLINE
            return Outcome.OFFLINE
        }
        return try {
            block().also {
                if (it == Outcome.DONE || it == Outcome.NOTHING) {
                    settings.lastSyncAt = System.currentTimeMillis()
                    settings.lastSyncError = null
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (t: Throwable) {
            val auth = t.isSteamAuthExpired()
            val msg = t.message ?: t.javaClass.simpleName
            LauncherLog.warn("Cloud $what failed: $msg")
            android.util.Log.e("Sts2Launcher.Cloud", "$what failed", t)
            settings.lastSyncError = if (auth) AUTH_EXPIRED else msg
            if (auth) Outcome.AUTH_EXPIRED else Outcome.FAILED
        }
    }

    private fun relative(file: java.io.File): String =
        runCatching { file.relativeTo(GamePrep.saveRoot(context)).invariantSeparatorsPath }.getOrDefault(file.name)

    companion object {
        /** Stored in SettingsStore.lastSyncError for the two failures the UI words itself. */
        const val OFFLINE = "offline"
        const val AUTH_EXPIRED = "auth_expired"
    }
}
