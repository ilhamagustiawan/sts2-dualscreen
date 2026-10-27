// GameSettingsFile — the performance keys in the game's own settings.save.
//
// The compat pack (Sts2MobileLauncher's, shipped in the runtime) reads its
// Android options from the account's settings.save next to the game's own
// settings: asset preloading, combat warmups, cheaper shaders, render size.
// Its patches that need no option (VFX pooling, the asset-loading frame
// budget) run regardless. Key names, defaults and the Recommended preset are
// Sts2MobileLauncher's ExtraSettingsRepository (MIT); see NOTICE.md.
//
// The game writes the file on its first run. It is never created here: a file
// the game did not write could be refused as corrupt. Until it exists the
// compat pack uses its own defaults, and ours apply from the next launch.

package dev.sts2ds.launcher

import android.content.Context
import org.json.JSONObject
import java.io.File

class GameSettingsFile(val file: File) {

    constructor(context: Context) : this(File(GamePrep.saveRoot(context), FILE_NAME))

    val exists: Boolean get() = file.isFile

    fun read(): JSONObject? = try {
        if (exists) JSONObject(file.readText()) else null
    } catch (e: Exception) {
        LauncherLog.log("settings.save unreadable: ${e.message}")
        null
    }

    /** Applies [change] and writes the file if it exists and anything changed. */
    fun update(change: (JSONObject) -> Boolean): Boolean {
        val json = read() ?: return false
        if (!change(json)) return false
        write(json)
        return true
    }

    fun write(json: JSONObject) {
        val tmp = File(file.parentFile, "$FILE_NAME.tmp")
        tmp.writeText(json.toString(2))
        if (!tmp.renameTo(file)) {
            tmp.delete()
            throw java.io.IOException("could not replace ${file.name}")
        }
    }

    companion object {
        const val FILE_NAME = "settings.save"

        const val KEY_MSAA = "msaa"
        const val KEY_VSYNC = "vsync"
        const val KEY_RENDER_SIZE = "fullscreen_render_size"
        const val KEY_SHADER_COMPAT = "shader_compatibility_mode"
        const val KEY_PRELOAD = "preload_enabled"
        const val KEY_OVERLAY = "android_performance_overlay_enabled"
        const val KEY_HIGH_REFRESH = "android_high_refresh_rate_enabled"

        /**
         * The compat pack's combat animation warmup steps each creature's
         * Spine clips as a fight loads; on game v0.107.1 it aborts the process
         * (a StringName refcount underflow, then a JNI abort) a few ms in.
         * Kept off, even in a file where it was turned on.
         */
        const val KEY_ANIMATION_WARMUP = "preload_combat_animation_warmup_mode"

        /** Always present; filled in only where missing so in-game changes stay. */
        private val BASE_DEFAULTS: Map<String, Any> = linkedMapOf(
            KEY_PRELOAD to true,
            "preload_startup_common_enabled" to true,
            "preload_startup_main_menu_enabled" to true,
            "preload_runtime_enabled" to true,
            "preload_protect_warm_cache_enabled" to true,
            "preload_learned_assets_enabled" to true,
            "preload_menu_hotspots_enabled" to false,
            "preload_vfx_tree_warmup_scope" to "safe",
            "preload_vfx_tree_warmup_frames" to 3,
            "preload_combat_animation_warmup_frames" to 1,
            "preload_shader_mode" to "off",
            "preload_combat_code_enabled" to false,
            KEY_SHADER_COMPAT to false,
            KEY_OVERLAY to false,
        )

        /**
         * Sts2MobileLauncher's "aggressive" preload profile: every room warms
         * its hit effects and VFX behind a mask as it loads, so the first
         * attack of a fight does not hitch. Off there by default; on here,
         * where the device has the memory for it. Its Spine animation warmup
         * stays off (see [KEY_ANIMATION_WARMUP]).
         */
        val WARMUPS_ON: Map<String, Any> = linkedMapOf(
            KEY_ANIMATION_WARMUP to "off",
            "preload_combat_hit_effect_warmup_enabled" to true,
            "preload_vfx_tree_warmup_enabled" to true,
            "preload_vfx_mode" to "hot",
            "preload_vfx_retain_cache_enabled" to true,
            "preload_gameplay_assets_enabled" to true,
        )

        val WARMUPS_OFF: Map<String, Any> = linkedMapOf(
            KEY_ANIMATION_WARMUP to "off",
            "preload_combat_hit_effect_warmup_enabled" to false,
            "preload_vfx_tree_warmup_enabled" to false,
            "preload_vfx_mode" to "off",
            "preload_vfx_retain_cache_enabled" to false,
            "preload_gameplay_assets_enabled" to false,
        )

        /** Sts2MobileLauncher's Recommended graphics preset, applied once. */
        private val RECOMMENDED_GRAPHICS: Map<String, Any> = linkedMapOf(
            KEY_MSAA to 0,
            KEY_VSYNC to "off",
        )

        val RENDER_SIZES = listOf(0 to 0, 1600 to 900, 1280 to 720)
        val MSAA_LEVELS = listOf(0, 2, 4, 8)
        val VSYNC_MODES = listOf("off", "on", "adaptive")

        /**
         * Fills in missing performance keys. [firstTime] also applies the
         * Recommended graphics preset and turns the warmups on over whatever
         * is there. Returns whether anything changed.
         */
        fun ensurePerformanceDefaults(json: JSONObject, firstTime: Boolean): Boolean {
            var changed = false
            for ((key, value) in BASE_DEFAULTS) changed = putIfMissing(json, key, value) || changed
            if (!json.has(KEY_RENDER_SIZE)) {
                putRenderSize(json, 0, 0)
                changed = true
            }
            if (firstTime) {
                changed = putAll(json, RECOMMENDED_GRAPHICS) || changed
                changed = putAll(json, WARMUPS_ON) || changed
            } else {
                for ((key, value) in WARMUPS_ON) changed = putIfMissing(json, key, value) || changed
            }
            if (json.optString(KEY_ANIMATION_WARMUP, "off") != "off") {
                json.put(KEY_ANIMATION_WARMUP, "off")
                changed = true
            }
            return changed
        }

        /**
         * The launcher's refresh mode after the compat pack's in-game "high
         * refresh rate" toggle: on means high; off undoes high and leaves 60 Hz
         * or system as they were.
         */
        fun refreshAfterGame(launcherMode: String, highInGame: Boolean?): String = when {
            highInGame == null -> launcherMode
            highInGame -> SettingsStore.REFRESH_HIGH
            launcherMode == SettingsStore.REFRESH_HIGH -> SettingsStore.REFRESH_SYSTEM
            else -> launcherMode
        }

        fun warmupsEnabled(json: JSONObject): Boolean =
            json.optBoolean("preload_combat_hit_effect_warmup_enabled", false) ||
                json.optBoolean("preload_vfx_tree_warmup_enabled", false)

        fun setWarmups(json: JSONObject, enabled: Boolean): Boolean =
            putAll(json, if (enabled) WARMUPS_ON else WARMUPS_OFF)

        fun renderSize(json: JSONObject): Pair<Int, Int> {
            val size = json.optJSONObject(KEY_RENDER_SIZE) ?: return 0 to 0
            return size.optInt("X", 0) to size.optInt("Y", 0)
        }

        /** Vector2I as the game serialises it; 0x0 is native. */
        fun putRenderSize(json: JSONObject, width: Int, height: Int) {
            json.put(KEY_RENDER_SIZE, JSONObject().put("X", width).put("Y", height))
        }

        private fun putIfMissing(json: JSONObject, key: String, value: Any): Boolean {
            if (json.has(key)) return false
            json.put(key, value)
            return true
        }

        private fun putAll(json: JSONObject, values: Map<String, Any>): Boolean {
            var changed = false
            for ((key, value) in values) {
                if (json.opt(key) != value) {
                    json.put(key, value)
                    changed = true
                }
            }
            return changed
        }
    }
}
