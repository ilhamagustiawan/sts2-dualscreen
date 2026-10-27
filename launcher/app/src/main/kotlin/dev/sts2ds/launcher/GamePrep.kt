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
// nothing changed, so it runs on every launch.

package dev.sts2ds.launcher

import android.content.Context
import android.system.Os
import dev.sts2ds.BuildConfig
import org.json.JSONArray
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

    class Prepared(
        val gameDir: File,
        val pck: File,
        val logsDir: File,
        val version: String,
        val compatTarget: String,
        val dualScreen: Boolean,
    )

    class NotReady(message: String) : IOException(message)

    fun gameDir(context: Context): File = DepotLocation.gameDir(context)

    /** True when a depot that looks bootable is on the device. */
    fun hasGame(context: Context): Boolean {
        val dir = gameDir(context)
        return File(dir, PCK_NAME).isFile && File(dir, "$ASSEMBLY_DIR/sts2.dll").isFile
    }

    fun version(context: Context): String = readVersion(gameDir(context))

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
        configureEnvironment(files)

        val state = context.getSharedPreferences("game_prep", Context.MODE_PRIVATE)
        val version = readVersion(gameDir)

        progress("Patching game package…")
        val pckStamp = "${pck.length()}:${pck.lastModified()}"
        if (force || state.getString("pck_stamp", "") != pckStamp) {
            val result = PckPatcher.patchSentry(pck)
            LauncherLog.log("PCK: ${result.toJson()}")
            state.edit().putString("pck_stamp", "${pck.length()}:${pck.lastModified()}").apply()
        }

        progress("Choosing compatibility pack…")
        val dllSha = sha256(gameDll)
        val compat = selectCompat(context, dllSha, version)
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
            state.edit().putString("bcl_stamp", bclStamp).apply()
        }
        copyIfDifferent(compat.dll, File(publish, COMPAT_DLL_NAME))

        val gameStamp = "${gameDir.absolutePath}:$dllSha"
        if (force || state.getString("game_stamp", "") != gameStamp) {
            copyGameAssemblies(assemblies, publish)
            state.edit().putString("game_stamp", gameStamp).apply()
        }

        progress("Writing launch context…")
        copyIfDifferent(compat.overlay, File(files, "port_compat.pck"))
        val accountRoot = File(files, "default/1").apply { mkdirs() }
        val modsDir = File(files, "mods").apply { mkdirs() }
        val logsDir = File(files, "logs").apply { mkdirs() }
        writeLaunchContext(files, gameDir, accountRoot, modsDir, logsDir, version, compat)

        val settings = SettingsStore(context)
        val dualScreen = settings.dualScreen
        File(files, "dualscreen.json").writeText(
            JSONObject()
                .put("enabled", dualScreen)
                .put("fps", settings.dualScreenFps)
                .put("default_tab", settings.dualScreenDefaultTab)
                .put("show_top_hint", settings.dualScreenTopHint)
                .toString(2)
        )

        return Prepared(gameDir, pck, logsDir, version, "${compat.packId}/${compat.targetId}", dualScreen)
    }

    /** Where the game keeps its saves on this device, as the compat pack maps them. */
    fun saveRoot(context: Context): File = File(context.filesDir, "default/1")

    // ── compat pack selection ──────────────────────────────────────────────

    private class Compat(val packId: String, val targetId: String, val dll: File, val overlay: File)

    private fun selectCompat(context: Context, dllSha: String, version: String): Compat {
        val full = readManifest(context, FULL_PACK)
        val target = full?.let { matchTarget(it, dllSha, version) }
        if (full != null && target != null) return extractTarget(context, FULL_PACK, full, target)

        val offline = readManifest(context, OFFLINE_PACK)
            ?: throw NotReady("No compatibility pack in this APK; rebuild with tools/runtime/make-runtime.sh")
        val any = offline.getJSONArray("targets").getJSONObject(0)
        LauncherLog.log("No exact compat target for $version; using the generic offline fallback")
        return extractTarget(context, OFFLINE_PACK, offline, any)
    }

    private fun matchTarget(manifest: JSONObject, dllSha: String, version: String): JSONObject? {
        val targets = manifest.optJSONArray("targets") ?: return null
        var byVersion: JSONObject? = null
        for (i in 0 until targets.length()) {
            val target = targets.getJSONObject(i)
            val shas = when (val v = target.opt("sts2_dll_sha256")) {
                is String -> listOf(v)
                is JSONArray -> (0 until v.length()).map { v.getString(it) }
                else -> emptyList()
            }
            if (shas.any { it.equals(dllSha, ignoreCase = true) }) return target
            val versions = target.optJSONArray("versions")
            if (byVersion == null && versions != null && version.isNotEmpty() &&
                (0 until versions.length()).any { versions.getString(it) == version }) {
                byVersion = target
            }
        }
        return byVersion
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
        return Compat(packId, targetId, dll, overlay)
    }

    // ── staging helpers ────────────────────────────────────────────────────

    private fun copyGameAssemblies(src: File, publish: File) {
        val names = src.listFiles().orEmpty()
            .filter { it.isFile && !isNative(it.name) && !isProtectedBcl(it.name) }
        // Drop game DLLs left over from a previous version.
        val keep = names.map { it.name }.toSet()
        publish.listFiles().orEmpty()
            .filter { it.isFile && (it.name.endsWith(".dll") || it.name.endsWith(".json")) }
            .filter { it.name !in keep && it.name.startsWith("sts2") }
            .forEach { it.delete() }
        for (file in names) copyIfDifferent(file, File(publish, file.name))
    }

    private fun isNative(name: String): Boolean {
        val lower = name.lowercase()
        // Windows natives (steam_api64.dll, sentry.dll, fmod.dll, ...) are not
        // managed assemblies and must not shadow the Android runtime.
        return lower.endsWith(".so") || lower.endsWith(".exe") || lower.endsWith(".pdb") ||
            lower in WINDOWS_NATIVES
    }

    private val WINDOWS_NATIVES = setOf(
        "steam_api64.dll", "sentry.dll", "fmod.dll", "fmodstudio.dll", "fmodl.dll",
        "fmodstudiol.dll", "libgodotfmod.windows.template_release.x86_64.dll",
        "libspine_godot.windows.template_release.x86_64.dll", "d3dcompiler_47.dll",
    )

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
     * Sts2MobileLauncher's AndroidRuntimeEnvironment).
     */
    private fun configureEnvironment(files: File) {
        File(files, ".config").mkdirs()
        val tmp = File(files, "tmp").apply { mkdirs() }
        try {
            Os.setenv("HOME", files.absolutePath, true)
            Os.setenv("TMPDIR", tmp.absolutePath, true)
            Os.setenv("TMP", tmp.absolutePath, true)
            Os.setenv("TEMP", tmp.absolutePath, true)
            System.setProperty("java.io.tmpdir", tmp.absolutePath)
        } catch (e: Exception) {
            LauncherLog.log("Could not set runtime environment: ${e.message}")
        }
    }

    private fun apkStamp(context: Context): Long =
        context.packageManager.getPackageInfo(context.packageName, 0).lastUpdateTime

    private fun copyAsset(context: Context, asset: String, dest: File) {
        context.assets.open(asset).use { input -> writeAtomically(dest) { input.copyTo(it) } }
    }

    private fun copyIfDifferent(src: File, dest: File) {
        if (dest.isFile && dest.length() == src.length() && sameContent(src, dest)) return
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
