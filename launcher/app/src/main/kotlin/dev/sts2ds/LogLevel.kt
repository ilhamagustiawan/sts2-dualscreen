// How much this build logs. Set per build type in build.gradle.kts and shared
// with libsts2ds (ds_log.h) and DsLog.cs, so the ordinals are the wire values.
//
//   WARN   release  warnings and failures
//   INFO   devel    + milestones and decisions
//   DEBUG  debug    + per-event detail

package dev.sts2ds

import android.util.Log

enum class LogLevel {
    WARN,
    INFO,
    DEBUG;

    /** True when a line at [line] level is logged at this level. */
    fun allows(line: LogLevel): Boolean = line.ordinal <= ordinal

    companion object {
        @JvmField val BUILD: LogLevel = entries[BuildConfig.LOG_LEVEL]

        /** The release build records no routine lines, so it has no Log screen. */
        val LOG_SCREEN: Boolean = BUILD.allows(INFO)
    }
}

/** logcat for the Java classes, which have no [dev.sts2ds.launcher.LauncherLog] panel. */
object AppLog {
    @JvmStatic fun i(tag: String, message: String) {
        if (LogLevel.BUILD.allows(LogLevel.INFO)) {
            Log.i(tag, message)
        }
    }

    @JvmStatic fun d(tag: String, message: String) {
        if (LogLevel.BUILD.allows(LogLevel.DEBUG)) {
            Log.d(tag, message)
        }
    }
}
