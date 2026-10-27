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

    /** "Choose your path on the lower screen" on the top screen while the map is open below. */
    var dualScreenTopHint: Boolean
        get() = prefs.getBoolean(KEY_DUAL_SCREEN_HINT, true)
        set(value) { prefs.edit().putBoolean(KEY_DUAL_SCREEN_HINT, value).apply() }

    fun clear() {
        prefs.edit().clear().apply()
    }

    companion object {
        private const val PREFS_NAME = "sts2ds_launcher_settings_v1"
        private const val KEY_AUTO_PULL = "auto_pull_before_launch"
        private const val KEY_AUTO_PUSH = "auto_push_after_play"
        private const val KEY_DUAL_SCREEN = "dual_screen"
        private const val KEY_DUAL_SCREEN_FPS = "dual_screen_fps"
        private const val KEY_DUAL_SCREEN_TAB = "dual_screen_default_tab"
        private const val KEY_DUAL_SCREEN_HINT = "dual_screen_top_hint"
        private const val KEY_PENDING_PUSH = "pending_push"
        private const val KEY_LAST_SYNC_AT = "last_sync_at"
        private const val KEY_LAST_SYNC_ERROR = "last_sync_error"
    }
}
