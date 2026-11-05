// GamePrep — turn a downloaded Windows depot into something the engine boots.
//
// The depot is the PC game: SlayTheSpire2.pck plus data_sts2_windows_x86_64/
// with sts2.dll and its managed dependencies. The custom Godot Mono engine
// (taken from Sts2MobileLauncher's release, see tools/runtime) expects, under
// the app's files dir:
//
//   .godot/mono/publish/arm64/   every managed assembly: the .NET class
//                                library, the game's DLLs, and STS2Mobile.dll,
//                                the entry the engine calls before the game
//   port_compat.pck              the compat pack's resource overlay
//   launcher/selected_instance.json
//                                where the compat pack finds the game dir,
//                                the save root and the mods dir
//
// STS2Mobile.dll is our entry shim (managed/STS2DsEntry). It loads the
// matching Sts2MobileLauncher compat pack, shipped next to it as
// STS2AndroidPortCompat.dll, and then the dual-screen layer.
//
// The staging order and rules follow Sts2MobileLauncher's
// GameLaunchPreparationManager; every step is idempotent and cheap when
// nothing changed, so it runs on every launch: in the launcher, and again in
// the game's process before the engine starts, where it must stay fast (the
// sts2.dll hash and the compat pack choice are cached on disk for that).

package dev.sts2ds.launcher

import android.content.Context
import android.content.SharedPreferences
import android.system.Os
import dev.sts2ds.BuildConfig
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import java.util.zip.ZipInputStream

object GamePrep {
    const val PCK_NAME = "SlayTheSpire2.pck"
    const val ASSEMBLY_DIR = "data_sts2_windows_x86_64"
    const val COMPAT_DLL_NAME = "STS2AndroidPortCompat.dll"
    private const val FULL_PACK = "compat_packs/sts2-android-compat.zip"
    private const val OFFLINE_PACK = "compat_packs/sts2-android-offline-bootstrap.zip"
    private const val OFFLINE_KIND = "offline-bootstrap"
    /** Names of the game files staged in publish last time. */
    private const val GAME_FILES_KEY = "game_files"

    /** Written by the game side (DsAnimWarmup) while a fight's animation warmup runs. */
    private const val ANIM_WARMUP_MARKER = "dualscreen_anim_warmup.running"

    class Prepared(
        val pck: File,
        val logsDir: File,
        val version: String,
        val dualScreen: Boolean,
        val renderer: String,
        val refreshRate: String,
        val inputPolicy: Boolean,
        /** Set only when the generic offline pack runs the game; see OfflineGuard. */
        val offlineTuple: String?,
    )

    class NotReady(message: String) : IOException(message)

    fun gameDir(context: Context): File = DepotLocation.gameDir(context)

    /** True when a depot that looks bootable is on the device. */
    fun hasGame(context: Context): Boolean {
        val dir = gameDir(context)
        return File(dir, PCK_NAME).isFile && File(dir, "$ASSEMBLY_DIR/sts2.dll").isFile
    }

    fun version(context: Context): String = readVersion(gameDir(context))

    /**
     * The game left the screen mid-warmup: if Android kills it now, that is
     * not a warmup crash, so the switch must not be turned off for it.
     */
    @JvmStatic
    fun clearAnimWarmupMarker(context: Context) {
        File(context.filesDir, ANIM_WARMUP_MARKER).delete()
    }

    @JvmStatic @JvmOverloads
    fun prepare(context: Context, force: Boolean, progress: (String) -> Unit = {}): Prepared {
        val files = context.filesDir
        val gameDir = gameDir(context)
        val pck = File(gameDir, PCK_NAME)
        val assemblies = File(gameDir, ASSEMBLY_DIR)
        val gameDll = File(assemblies, "sts2.dll")
        if (!pck.isFile || !gameDll.isFile) {
            throw NotReady("Slay the Spire 2 is not downloaded yet")
        }
        val started = System.nanoTime()
        val settings = SettingsStore(context)
        configureEnvironment(files, settings.gcNursery)

        val state = context.getSharedPreferences("game_prep", Context.MODE_PRIVATE)
        val version = readVersion(gameDir)

        progress("Patching game package…")
        // Re-patched when the patcher learns a new form (its schema), too.
        val pckStamp = "${pck.length()}:${pck.lastModified()}"
        val pckSchemaStale = state.getInt("pck_patch_schema", 0) != PckPatcher.PATCH_SCHEMA
        if (force || pckSchemaStale || state.getString("pck_stamp", "") != pckStamp) {
            val result = PckPatcher.patchSentry(pck)
            LauncherLog.log("PCK: ${result.toJson()}")
            state.edit()
                .putString("pck_stamp", "${pck.length()}:${pck.lastModified()}")
                .putInt("pck_patch_schema", PckPatcher.PATCH_SCHEMA)
                .commit()
        }

        progress("Choosing compatibility pack…")
        val dllSha = gameDllSha(state, gameDll, force)
        clearBuildCaches(files, state, "$dllSha:${state.getString("pck_stamp", "")}")
        val compatStamp = "${apkStamp(context)}:$dllSha:$version"
        val cachedCompat = if (force) null else cachedCompat(context, state, compatStamp)
        val compat = cachedCompat ?: selectCompat(context, dllSha, version).also {
            state.edit()
                .putString("compat_stamp", compatStamp)
                .putString("compat_pack", it.packId)
                .putString("compat_target", it.targetId)
                .putString("compat_kind", it.kind)
                .putString("compat_version", it.version)
                .commit()
        }
        LauncherLog.log("Game $version (sts2.dll ${dllSha.take(12)}) → compat ${compat.packId}/${compat.targetId}")

        progress("Staging assemblies…")
        val publish = File(files, ".godot/mono/publish/arm64")
        publish.mkdirs()
        val bclStamp = "${BuildConfig.VERSION_CODE}:${BuildConfig.VERSION_NAME}:${apkStamp(context)}"
        if (force || state.getString("bcl_stamp", "") != bclStamp || !File(publish, "GodotSharp.dll").isFile) {
            for (name in context.assets.list("dotnet_bcl").orEmpty()) {
                // The runtime APK's STS2Mobile.dll is its own compat entry; ours
                // replaces it below.
                if (name == "STS2Mobile.dll") continue
                copyAsset(context, "dotnet_bcl/$name", File(publish, name))
            }
            for (name in context.assets.list("dualscreen").orEmpty()) {
                copyAsset(context, "dualscreen/$name", File(publish, name))
            }
            state.edit().putString("bcl_stamp", bclStamp).commit()
        }
        stageCompatFile(compat.dll, File(publish, COMPAT_DLL_NAME), cachedCompat == null)

        val gameStamp = "${gameDir.absolutePath}:$dllSha:$bclStamp"
        if (force || state.getString("game_stamp", "") != gameStamp) {
            val runtimeNames = context.assets.list("dotnet_bcl").orEmpty().toSet() +
                context.assets.list("dualscreen").orEmpty() + COMPAT_DLL_NAME
            copyGameAssemblies(assemblies, publish, runtimeNames, state)
            state.edit().putString("game_stamp", gameStamp).commit()
        }

        progress("Writing launch context…")
        stageCompatFile(compat.overlay, File(files, "port_compat.pck"), cachedCompat == null)
        val accountRoot = File(files, "default/1").apply { mkdirs() }
        val modsDir = File(files, "mods").apply { mkdirs() }
        val logsDir = File(files, "logs").apply { mkdirs() }
        writeLaunchContext(files, gameDir, accountRoot, modsDir, logsDir, version, compat)

        // Left by an older build whose animation warmup could be on.
        File(files, ANIM_WARMUP_MARKER).delete()

        val dualScreen = settings.dualScreen
        File(files, "dualscreen.json").writeText(
            JSONObject()
                .put("enabled", dualScreen)
                .put("fps", settings.dualScreenFps)
                .put("default_tab", settings.dualScreenDefaultTab)
                .put("particle_amount", settings.particleAmount / 100.0)
                .put("build", "$dllSha:${state.getString("pck_stamp", "")}")
                // The warmup cache trusts only a pipeline cache written after this.
                .put("build_time_ms", maxOf(pck.lastModified(), gameDll.lastModified()))
                .put("perf_hint", settings.perfHint)
                .put("compat", "${compat.packId}/${compat.targetId}")
                .let { putCaptureSwitches(it, settings.presentThread, settings.staggerCaptures, settings.pauseHiddenPages) }
                .toString(2)
        )
        applyPerformanceSettings(context, files, settings)
        try {
            EngineOverrides.apply(gameDir, EngineOverrides.render(settings.savePipelinesOften, settings.framePacing))
            LauncherLog.log(
                "Engine overrides: frame pacing ${settings.framePacing}, " +
                    "pipeline cache saved ${if (settings.savePipelinesOften) "every ${EngineOverrides.PIPELINE_SAVE_CHUNK_MB} MB" else "at engine default"}, " +
                    "perf hint ${settings.perfHint}"
            )
        } catch (e: Exception) {
            LauncherLog.warn("Could not write engine overrides: ${e.message}")
        }

        LauncherLog.log(
            "Prepared in ${(System.nanoTime() - started) / 1_000_000} ms" +
                if (cachedCompat != null) " (cached)" else " (full)"
        )
        return Prepared(
            pck, logsDir, version, dualScreen,
            settings.renderer, settings.refreshRate, settings.inputPolicy,
            if (compat.isOffline) OfflineGuard.tuple(compat.packId, compat.targetId, compat.version, version, dllSha) else null,
        )
    }

    /**
     * The compat pack's performance options live in the game's settings.save
     * (see GameSettingsFile); the overlay is a flag file it looks for. A failure
     * here only costs the defaults, never the launch.
     */
    private fun applyPerformanceSettings(context: Context, files: File, settings: SettingsStore) {
        val game = GameSettingsFile(context)
        try {
            val json = game.read()
            if (json == null) {
                LauncherLog.log("settings.save not created yet; performance defaults apply next launch")
            } else {
                val firstTime = !settings.perfDefaultsApplied
                // The Balanced preset locks the panel to 60 Hz, over the
                // compat pack's in-game toggle too.
                val refresh = if (firstTime) SettingsStore.REFRESH_60HZ else GameSettingsFile.refreshAfterGame(
                    settings.refreshRate,
                    if (json.has(GameSettingsFile.KEY_HIGH_REFRESH)) json.optBoolean(GameSettingsFile.KEY_HIGH_REFRESH) else null,
                )
                if (refresh != settings.refreshRate) settings.refreshRate = refresh
                var changed = GameSettingsFile.ensurePerformanceDefaults(
                    json, firstTime, settings.combatCodeWarmup,
                )
                val high = refresh == SettingsStore.REFRESH_HIGH
                if (json.opt(GameSettingsFile.KEY_HIGH_REFRESH) != high) {
                    json.put(GameSettingsFile.KEY_HIGH_REFRESH, high)
                    changed = true
                }
                if (changed) game.write(json)
                if (firstTime) settings.perfDefaultsApplied = true
                val (w, h) = GameSettingsFile.renderSize(json)
                LauncherLog.log(
                    "Performance: renderer ${settings.renderer}, refresh $refresh, " +
                        "render ${if (w > 0) "${w}x$h" else "native"}, msaa ${json.opt(GameSettingsFile.KEY_MSAA)}, " +
                        "vsync ${json.opt(GameSettingsFile.KEY_VSYNC)}, fps_limit ${json.opt(GameSettingsFile.KEY_FPS_LIMIT)}, " +
                        "skip_intro ${json.opt(GameSettingsFile.KEY_SKIP_INTRO)}, warmups ${GameSettingsFile.warmupsEnabled(json)}, " +
                        "combat_code ${settings.combatCodeWarmup}" +
                        if (firstTime) " (defaults applied)" else ""
                )
            }
            val overlay = json?.optBoolean(GameSettingsFile.KEY_OVERLAY, false) == true
            val flag = File(files, "launcher/enable_debug_menu.flag")
            if (overlay) flag.writeText("1") else flag.delete()
        } catch (e: Exception) {
            LauncherLog.warn("Could not apply performance settings: ${e.message}")
        }
    }

    /** Where the game keeps its saves on this device, as the compat pack maps them. */
    fun saveRoot(context: Context): File = File(context.filesDir, "default/1")

    /** Texture caches under the files dir; see [clearBuildCaches]. */
    val BUILD_CACHE_DIRS = listOf("etc2_cache/.godot/imported", ".godot/imported")

    // ── compat pack selection ──────────────────────────────────────────────

    /**
     * sts2.dll's SHA-256, cached by path, size and time: hashing it on every
     * launch, twice, holds up the engine's start. Stamps are written with
     * commit(): the game's process reads them a second later.
     */
    private fun gameDllSha(state: SharedPreferences, dll: File, force: Boolean): String {
        val stamp = "${dll.absolutePath}:${dll.length()}:${dll.lastModified()}"
        val cached = state.getString("dll_sha", null)
        if (!force && cached != null && state.getString("dll_sha_stamp", "") == stamp) return cached
        val sha = sha256(dll)
        state.edit().putString("dll_sha_stamp", stamp).putString("dll_sha", sha).commit()
        return sha
    }

    /**
     * The engine's converted copies of the game's textures (ETC2, and Godot's
     * own import cache) are named by resource path, not content, so they
     * outlive an update or a switch between stable and beta and keep serving
     * the old build's card art and relic icons. Dropped whenever the game
     * build changes, as Sts2MobileLauncher's GameLaunchPreparationManager does
     * on a PCK change; the first launch after that converts them again.
     * Shader caches stay: Godot keys those by shader source. So does the
     * compat pack's learned preload list, which checks the build itself.
     */
    private fun clearBuildCaches(files: File, state: SharedPreferences, build: String) {
        // MonoMod leaves a dump of every patched method in TMPDIR, each launch.
        files.resolve("tmp").listFiles { f -> f.name.startsWith("MonoMod_DMD_") }?.forEach { it.delete() }
        if (state.getString("cache_build", "") == build) return
        File(files, "dualscreen_warmup.json").delete()
        var bytes = 0L
        for (cache in BUILD_CACHE_DIRS.map { File(files, it) }) {
            if (!cache.exists()) continue
            bytes += cache.walkBottomUp().filter { it.isFile }.sumOf { it.length() }
            cache.deleteRecursively()
        }
        state.edit().putString("cache_build", build).commit()
        LauncherLog.log("Game build changed: cleared the texture cache (${bytes / (1 shl 20)} MB)")
    }

    /** The compat target chosen last time for this APK and game, if its files are still there. */
    private fun cachedCompat(context: Context, state: SharedPreferences, stamp: String): Compat? {
        if (state.getString("compat_stamp", "") != stamp) return null
        // Chosen before the pack's kind was recorded: choose again.
        if (!state.contains("compat_kind")) return null
        val packId = state.getString("compat_pack", null) ?: return null
        val targetId = state.getString("compat_target", null) ?: return null
        val dir = File(context.filesDir, "compat/$packId/$targetId")
        val dll = File(dir, "STS2Mobile.dll")
        val overlay = File(dir, "port_compat.pck")
        val kind = state.getString("compat_kind", "").orEmpty()
        val version = state.getString("compat_version", "").orEmpty()
        return if (dll.isFile && overlay.isFile) Compat(packId, targetId, kind, version, dll, overlay) else null
    }

    /** [kind] is the manifest's pack_kind ("" for a full pack); [version] its compat_version. */
    private class Compat(
        val packId: String,
        val targetId: String,
        val kind: String,
        val version: String,
        val dll: File,
        val overlay: File,
    ) {
        val isOffline: Boolean get() = kind == OFFLINE_KIND
    }

    private fun selectCompat(context: Context, dllSha: String, version: String): Compat {
        val full = readManifest(context, FULL_PACK)
        val target = full?.let { CompatMatch.target(it, dllSha, version) }
        if (full != null && target != null) return extractTarget(context, FULL_PACK, full, target)

        val offline = readManifest(context, OFFLINE_PACK)
            ?: throw NotReady("No compatibility pack in this APK; rebuild with tools/runtime/make-runtime.sh")
        val any = offline.getJSONArray("targets").getJSONObject(0)
        LauncherLog.log("No exact compat target for $version; using the generic offline fallback")
        return extractTarget(context, OFFLINE_PACK, offline, any)
    }

    private fun readManifest(context: Context, asset: String): JSONObject? = try {
        context.assets.open(asset).use { input ->
            ZipInputStream(input).use { zip ->
                generateSequence { zip.nextEntry }
                    .firstOrNull { it.name.endsWith("/compat_manifest.json") }
                    ?.let { JSONObject(zip.readBytes().decodeToString()) }
            }
        }
    } catch (_: IOException) {
        null
    }

    private fun extractTarget(context: Context, asset: String, manifest: JSONObject, target: JSONObject): Compat {
        val packId = manifest.getString("pack_id")
        val targetId = target.getString("target_id")
        val artifacts = target.getJSONObject("artifacts")
        val dir = File(context.filesDir, "compat/$packId/$targetId").apply { mkdirs() }
        val dll = File(dir, "STS2Mobile.dll")
        val overlay = File(dir, "port_compat.pck")
        val wanted = mapOf(artifacts.getString("dll") to dll, artifacts.getString("overlay_pck") to overlay)
        context.assets.open(asset).use { input ->
            ZipInputStream(input).use { zip ->
                for (entry in generateSequence { zip.nextEntry }) {
                    val relative = entry.name.substringAfter('/')
                    val dest = wanted[relative] ?: continue
                    writeAtomically(dest) { out -> zip.copyTo(out) }
                }
            }
        }
        if (!dll.isFile || !overlay.isFile) throw NotReady("Compat pack $packId/$targetId is incomplete")

        // A target rebuilt from source (make compat) carries its own commit.
        val build = target.optJSONObject("build_info") ?: manifest.optJSONObject("build_info")
        LauncherLog.log("Compat $packId/$targetId built from ${build?.optString("commit") ?: "unknown"}")
        val kind = manifest.optString("pack_kind", "")
        val version = manifest.optString("compat_version", "")
        return Compat(packId, targetId, kind, version, dll, overlay)
    }

    // ── staging helpers ────────────────────────────────────────────────────

    /**
     * The game's own files, minus anything the Android runtime provides: the
     * depot carries Windows CoreCLR builds of framework assemblies (and its
     * own Harmony/MonoMod) that must not replace the Android ones.
     */
    private fun copyGameAssemblies(src: File, publish: File, runtimeNames: Set<String>, state: SharedPreferences) {
        val names = src.listFiles().orEmpty()
            .filter { it.isFile && !isNative(it.name) && !isProtectedBcl(it.name) && it.name !in runtimeNames }
        // Drop game files left over from a previous version: those it staged
        // (recorded since), and any sts2* DLL or JSON.
        val keep = names.map { it.name }.toSet()
        val staged = state.getStringSet(GAME_FILES_KEY, emptySet()).orEmpty()
        publish.listFiles().orEmpty()
            .filter { it.isFile && it.name !in keep && it.name !in runtimeNames }
            .filter { it.name in staged || isGameDllOrJson(it.name) }
            .forEach { it.delete() }

        for (file in names) copyIfDifferent(file, File(publish, file.name))
        state.edit().putStringSet(GAME_FILES_KEY, keep).commit()
    }

    private fun isGameDllOrJson(name: String): Boolean =
        name.startsWith("sts2") && (name.endsWith(".dll") || name.endsWith(".json"))

    // Everything but .so files goes across, as Sts2MobileLauncher does. Do not
    // filter Windows "natives" by name: Sentry.dll, for one, is the managed
    // Sentry SDK the game's root node loads, and without it nothing renders.
    // Native Windows DLLs in the publish dir are inert on Android.
    private fun isNative(name: String): Boolean = name.lowercase().endsWith(".so")

    private fun isProtectedBcl(name: String): Boolean =
        name.startsWith("System.") || name == "mscorlib.dll" || name == "netstandard.dll" ||
            name == "Microsoft.CSharp.dll" || name == "Microsoft.VisualBasic.dll" ||
            name == "Microsoft.VisualBasic.Core.dll" || name == "GodotSharp.dll" ||
            name == "0Harmony.dll" || name.startsWith("MonoMod.") || name.startsWith("Mono.Cecil")

    private fun writeLaunchContext(
        files: File, gameDir: File, accountRoot: File, modsDir: File, logsDir: File,
        version: String, compat: Compat,
    ) {
        val root = JSONObject()
            .put("schema", 1)
            .put("data_dir", files.absolutePath)
            .put("selected_instance_id", "default")
            .put("selected_profile_id", "default")
            .put("instance_id", "default")
            .put("profile_id", "default")
            .put("display_name", "Slay the Spire 2")
            .put("payload_version", version)
            .put("selected_game_dir", gameDir.absolutePath)
            .put("selected_release_info", File(gameDir, "release_info.json").absolutePath)
            .put("save_mode", "shared")
            .put("mods_mode", "shared")
            .put("selected_account_root", accountRoot.absolutePath)
            .put("selected_settings_path", File(accountRoot, "settings.save").absolutePath)
            .put("selected_mods_dir", modsDir.absolutePath)
            .put("selected_logs_dir", logsDir.absolutePath)
            .put("compat_pack_id", compat.packId)
            .put("compat_target_id", compat.targetId)
            .put("selected_compat_target_id", compat.targetId)
            .put("selected_compat_pack_dir", compat.dll.parentFile!!.absolutePath)
            .put("selected_compat_dll", compat.dll.absolutePath)
            .put("selected_compat_overlay_pck", compat.overlay.absolutePath)
        val dir = File(files, "launcher").apply { mkdirs() }
        File(dir, "selected_instance.json").writeText(root.toString(2))
    }

    private fun readVersion(gameDir: File): String = try {
        JSONObject(File(gameDir, "release_info.json").readText()).optString("version", "")
    } catch (_: Exception) {
        ""
    }

    /**
     * HOME and TMPDIR must be writable before Mono starts: it derives
     * ApplicationData from HOME/.config and caches it (see
     * Sts2MobileLauncher's AndroidRuntimeEnvironment). MONO_GC_PARAMS too:
     * SGen reads it once, as the engine brings Mono up after onCreate.
     */
    private fun configureEnvironment(files: File, gcNursery: String) {
        File(files, ".config").mkdirs()
        val tmp = File(files, "tmp").apply { mkdirs() }
        try {
            Os.setenv("HOME", files.absolutePath, true)
            Os.setenv("TMPDIR", tmp.absolutePath, true)
            Os.setenv("TMP", tmp.absolutePath, true)
            Os.setenv("TEMP", tmp.absolutePath, true)
            System.setProperty("java.io.tmpdir", tmp.absolutePath)
            val gc = gcParams(gcNursery)
            if (gc != null) Os.setenv("MONO_GC_PARAMS", gc, true) else Os.unsetenv("MONO_GC_PARAMS")
            LauncherLog.log("MONO_GC_PARAMS=${gc ?: "(runtime default)"}")
        } catch (e: Exception) {
            LauncherLog.warn("Could not set runtime environment: ${e.message}")
        }
    }

    /**
     * SGen options for the launcher's GC setting, or null for the runtime's
     * own. The shipped libmonosgen-2.0.so (Mono 9.0.7) takes nursery-size as
     * a power of two (anything else is ignored with a warning) and reports
     * "sgen (concurrent by default)", so major=marksweep-conc only says
     * explicitly what it already does.
     */
    /**
     * The lower screen's capture switches, under the keys DsConfig reads
     * (dualscreen_overrides.json, laid over this file, still wins).
     */
    fun putCaptureSwitches(json: JSONObject, presentThread: Boolean, stagger: Boolean, pauseHidden: Boolean): JSONObject =
        json.put("present_thread", presentThread)
            .put("stagger_captures", stagger)
            .put("pause_hidden_pages", pauseHidden)

    fun gcParams(nursery: String): String? = when (nursery) {
        SettingsStore.GC_16M -> "nursery-size=16m,major=marksweep-conc"
        SettingsStore.GC_32M -> "nursery-size=32m,major=marksweep-conc"
        else -> null
    }

    private fun apkStamp(context: Context): Long =
        context.packageManager.getPackageInfo(context.packageName, 0).lastUpdateTime

    private fun copyAsset(context: Context, asset: String, dest: File) {
        context.assets.open(asset).use { input -> writeAtomically(dest) { input.copyTo(it) } }
    }

    /**
     * A freshly extracted compat file is compared byte for byte; a cached one
     * was staged by an earlier launch, so only a missing or resized copy is
     * redone.
     */
    private fun stageCompatFile(src: File, dest: File, fresh: Boolean) {
        if (fresh) copyIfDifferent(src, dest)
        else if (!dest.isFile || dest.length() != src.length()) copyFile(src, dest)
    }

    private fun copyIfDifferent(src: File, dest: File) {
        if (dest.isFile && dest.length() == src.length() && sameContent(src, dest)) return
        copyFile(src, dest)
    }

    private fun copyFile(src: File, dest: File) {
        FileInputStream(src).use { input -> writeAtomically(dest) { input.copyTo(it) } }
    }

    private fun sameContent(a: File, b: File): Boolean {
        FileInputStream(a).use { x ->
            FileInputStream(b).use { y ->
                val bx = ByteArray(1 shl 16)
                val by = ByteArray(1 shl 16)
                while (true) {
                    val nx = readFully(x, bx)
                    val ny = readFully(y, by)
                    if (nx != ny) return false
                    if (nx <= 0) return true
                    for (i in 0 until nx) if (bx[i] != by[i]) return false
                }
            }
        }
    }

    private fun readFully(input: InputStream, buffer: ByteArray): Int {
        var total = 0
        while (total < buffer.size) {
            val n = input.read(buffer, total, buffer.size - total)
            if (n < 0) break
            total += n
        }
        return total
    }

    private fun writeAtomically(dest: File, write: (java.io.OutputStream) -> Unit) {
        dest.parentFile?.mkdirs()
        val temp = File(dest.parentFile, ".${dest.name}.tmp")
        temp.outputStream().use(write)
        if (!temp.renameTo(dest)) {
            temp.delete()
            throw IOException("Could not write $dest")
        }
    }

    fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        FileInputStream(file).use { input ->
            val buffer = ByteArray(1 shl 16)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                digest.update(buffer, 0, n)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
