// SettingsStore — the launcher's settings, in one SharedPreferences file.
//
// Adapted from SilksongAndroid's SettingsStore (MIT). The launcher process
// and the game process share package data, but the game reads none of these
// directly: GamePrep writes what the game needs (dualscreen.json) right
// before each launch.

package dev.sts2ds.launcher

import android.content.Context
import android.content.SharedPreferences

class SettingsStore(context: Context) {

    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** Download newer cloud saves right before the game starts. */
    var autoPull: Boolean
        get() = prefs.getBoolean(KEY_AUTO_PULL, true)
        set(value) { prefs.edit().putBoolean(KEY_AUTO_PULL, value).apply() }

    /** Upload changed saves when you come back from the game. */
    var autoPush: Boolean
        get() = prefs.getBoolean(KEY_AUTO_PUSH, true)
        set(value) { prefs.edit().putBoolean(KEY_AUTO_PUSH, value).apply() }

    /**
     * Saves on this device have not reached the cloud yet: set as the game
     * starts, cleared by a push. On disk rather than in the activity because
     * Android may kill the launcher's process while the game runs.
     */
    var pendingPush: Boolean
        get() = prefs.getBoolean(KEY_PENDING_PUSH, false)
        set(value) { prefs.edit().putBoolean(KEY_PENDING_PUSH, value).apply() }

    /** When saves last finished syncing, in epoch millis; 0 if never. */
    var lastSyncAt: Long
        get() = prefs.getLong(KEY_LAST_SYNC_AT, 0L)
        set(value) { prefs.edit().putLong(KEY_LAST_SYNC_AT, value).apply() }

    /** Why the last sync failed, or null when it did not. */
    var lastSyncError: String?
        get() = prefs.getString(KEY_LAST_SYNC_ERROR, null)
        set(value) { prefs.edit().putString(KEY_LAST_SYNC_ERROR, value).apply() }

    /** Map and deck on the second screen. Does nothing on a single-screen device. */
    var dualScreen: Boolean
        get() = prefs.getBoolean(KEY_DUAL_SCREEN, true)
        set(value) { prefs.edit().putBoolean(KEY_DUAL_SCREEN, value).apply() }

    /** Bottom-screen refresh cap: 30 saves battery, 60 is smoother. */
    var dualScreenFps: Int
        get() = prefs.getInt(KEY_DUAL_SCREEN_FPS, 30)
        set(value) { prefs.edit().putInt(KEY_DUAL_SCREEN_FPS, value).apply() }

    /** Tab the bottom screen opens on: map, draw, discard, exhaust or deck. */
    var dualScreenDefaultTab: String
        get() = prefs.getString(KEY_DUAL_SCREEN_TAB, "map") ?: "map"
        set(value) { prefs.edit().putString(KEY_DUAL_SCREEN_TAB, value).apply() }

    /**
     * The Steam branch the game is downloaded from: [BRANCH_STABLE] or
     * [BRANCH_BETA]. A change takes effect with the next download, which only
     * fetches the files that differ.
     */
    var gameBranch: String
        get() = prefs.getString(KEY_GAME_BRANCH, BRANCH_STABLE) ?: BRANCH_STABLE
        set(value) { prefs.edit().putString(KEY_GAME_BRANCH, value).apply() }

    /**
     * Percent of each particle emitter's particles the game draws: 100, 50 or
     * 25. Half by default; the effects still read and cost half the fill.
     */
    var particleAmount: Int
        get() = prefs.getInt(KEY_PARTICLE_AMOUNT, 50)
        set(value) { prefs.edit().putInt(KEY_PARTICLE_AMOUNT, value).apply() }

    /** "vulkan", or "opengl" for the engine's Compatibility renderer. */
    var renderer: String
        get() = prefs.getString(KEY_RENDERER, RENDERER_VULKAN) ?: RENDERER_VULKAN
        set(value) { prefs.edit().putString(KEY_RENDERER, value).apply() }

    /**
     * Top-screen refresh rate while the game runs: "system", "60hz" or "high".
     * 60 Hz by default: the game is capped at 60, and a 120 Hz panel showing
     * 60 fps without vsync paces frames unevenly.
     */
    var refreshRate: String
        get() = prefs.getString(KEY_REFRESH_RATE, REFRESH_60HZ) ?: REFRESH_60HZ
        set(value) { prefs.edit().putString(KEY_REFRESH_RATE, value).apply() }

    /**
     * The one-time performance defaults have gone into settings.save. The key
     * is versioned: a new preset gets a new key and so applies once more.
     */
    var perfDefaultsApplied: Boolean
        get() = prefs.getBoolean(KEY_PERF_DEFAULTS, false)
        set(value) { prefs.edit().putBoolean(KEY_PERF_DEFAULTS, value).commit() }

    /**
     * Pre-warm creature Spine animations when entering combat rooms. Off by
     * default: the compat pack's warmup aborts the engine ("Unreferenced
     * static string to 0: markers") on some fights.
     */
    var animWarmup: Boolean
        get() = prefs.getBoolean(KEY_ANIM_WARMUP, false)
        set(value) { prefs.edit().putBoolean(KEY_ANIM_WARMUP, value).apply() }

    /**
     * Have the compat pack JIT-compile the combat code while the game loads
     * (GameSettingsFile.KEY_COMBAT_CODE_WARMUP). On by default: it only
     * prepares methods, so it costs a little load time and no fight stutters
     * on its first attack's code. Written into settings.save every launch.
     */
    var combatCodeWarmup: Boolean
        get() = prefs.getBoolean(KEY_COMBAT_CODE_WARMUP, true)
        set(value) { prefs.edit().putBoolean(KEY_COMBAT_CODE_WARMUP, value).apply() }

    /**
     * Mono's garbage collector young generation (nursery): [GC_DEFAULT] for
     * the runtime's own (4 MB), or [GC_16M]/[GC_32M]. A bigger nursery fills
     * up less often, so there are fewer minor collections, each a pause on
     * the game thread, while cards and effects churn objects. 32 MB by
     * default: the Thor has the memory, and pause time grows with what
     * survives, not with the nursery's size. GamePrep sets MONO_GC_PARAMS.
     */
    var gcNursery: String
        get() = prefs.getString(KEY_GC_NURSERY, GC_32M) ?: GC_32M
        set(value) { prefs.edit().putString(KEY_GC_NURSERY, value).apply() }

    /**
     * Tell Android how long each game frame's work takes against a one-frame
     * target (ADPF, Android 13+), so CPU clocks rise as a burst of work
     * starts. On by default; does nothing on devices without it.
     */
    var perfHint: Boolean
        get() = prefs.getBoolean(KEY_PERF_HINT, true)
        set(value) { prefs.edit().putBoolean(KEY_PERF_HINT, value).apply() }

    /** Flag set when a combat room warmup crashed, so the launcher can notify the user. */
    var animWarmupCrashed: Boolean
        get() = prefs.getBoolean(KEY_ANIM_WARMUP_CRASHED, false)
        set(value) { prefs.edit().putBoolean(KEY_ANIM_WARMUP_CRASHED, value).apply() }

    fun clear() {
        prefs.edit().clear().apply()
    }

    companion object {
        const val RENDERER_VULKAN = "vulkan"
        const val RENDERER_OPENGL = "opengl"
        const val REFRESH_SYSTEM = "system"
        const val REFRESH_60HZ = "60hz"
        const val REFRESH_HIGH = "high"
        const val GC_DEFAULT = "default"
        const val GC_16M = "16m"
        const val GC_32M = "32m"
        const val BRANCH_STABLE = "public"
        const val BRANCH_BETA = "public-beta"

        /** "Stable" or "Beta", for [gameBranch]. */
        fun branchName(context: Context, branch: String): String =
            context.getString(if (branch == BRANCH_BETA) dev.sts2ds.R.string.branch_beta else dev.sts2ds.R.string.branch_stable)

        private const val PREFS_NAME = "sts2ds_launcher_settings_v1"
        private const val KEY_RENDERER = "renderer"
        private const val KEY_GAME_BRANCH = "game_branch"
        private const val KEY_PARTICLE_AMOUNT = "particle_amount"
        private const val KEY_REFRESH_RATE = "refresh_rate"
        private const val KEY_PERF_DEFAULTS = "perf_defaults_v3"
        private const val KEY_ANIM_WARMUP = "anim_warmup"
        private const val KEY_ANIM_WARMUP_CRASHED = "anim_warmup_crashed"
        private const val KEY_COMBAT_CODE_WARMUP = "combat_code_warmup"
        private const val KEY_GC_NURSERY = "gc_nursery"
        private const val KEY_PERF_HINT = "perf_hint"
        private const val KEY_AUTO_PULL = "auto_pull_before_launch"
        private const val KEY_AUTO_PUSH = "auto_push_after_play"
        private const val KEY_DUAL_SCREEN = "dual_screen"
        private const val KEY_DUAL_SCREEN_FPS = "dual_screen_fps"
        private const val KEY_DUAL_SCREEN_TAB = "dual_screen_default_tab"
        private const val KEY_PENDING_PUSH = "pending_push"
        private const val KEY_LAST_SYNC_AT = "last_sync_at"
        private const val KEY_LAST_SYNC_ERROR = "last_sync_error"
    }
}
