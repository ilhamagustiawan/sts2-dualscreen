// GameReset — throw away what GamePrep staged, keep everything expensive.
//
// The idea is SilksongAndroid's BuildReset (MIT): a staged game can end up
// wrong in ways nothing detects, and "clear app data" would also take the
// multi-gigabyte download and the saves. This deletes only what GamePrep
// recreates on the next launch.

package dev.sts2ds.launcher

import android.content.Context
import java.io.File

object GameReset {
    fun clear(context: Context) {
        val files = context.filesDir
        for (name in listOf(".godot", "compat", "port_compat.pck", "launcher", "dualscreen.json", "dualscreen_warmup.json", "dualscreen_anim_warmup.running", "tmp") + GamePrep.BUILD_CACHE_DIRS) {
            File(files, name).deleteRecursively()
        }
        context.getSharedPreferences("game_prep", Context.MODE_PRIVATE).edit().clear().apply()
        LauncherLog.log("Reset: staged game removed; saves, download and Steam sign-in kept")
    }
}
