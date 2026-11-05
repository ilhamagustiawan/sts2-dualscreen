// Sts2App — the Application class: runs once per process, before any activity.
//
// Adapted from SilksongAndroid's SilksongApp (MIT): the launcher log, a crash
// handler that records uncaught exceptions from every process, and
// BouncyCastle for JavaSteam.

package dev.sts2ds.launcher

import android.app.Application
import android.os.Build

class Sts2App : Application() {

    override fun onCreate() {
        super.onCreate()

        // First, so that everything below is recorded too. The log is written
        // to a file from here on, which is what makes it survivable enough to
        // be worth asking a user for. It also drains whatever was logged
        // before, which had nowhere to go yet.
        LauncherLog.attach(this)

        // Which process this is. The log is shared by the launcher and the
        // game, and every session in it opens with the same header, so this
        // line is what tells them apart -- and makes "the game's process
        // started and then nothing" a readable fact.
        LauncherLog.log("process ${processTag()} started")

        // Before the engine starts, so a crash during its startup is recorded.
        installCrashHandler()

        // Before anything else, and in every process: JavaSteam's crypto
        // registers itself the first time it is touched, and on Android it
        // registers the wrong thing unless this has run first. See SteamCrypto.
        SteamCrypto.install()
    }

    /**
     * Uncaught exceptions, in every process, recorded before the process goes.
     * Chained, never swallowed: the previous handler is what ends the process.
     */
    private fun installCrashHandler() {
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            // Through LauncherLog because it appends and closes per line, so
            // the line is on disk before the process is taken away.
            runCatching {
                val stack = java.io.StringWriter()
                error.printStackTrace(java.io.PrintWriter(stack))
                LauncherLog.warn(
                    "FATAL in ${processTag()} on thread ${thread.name}\n" +
                        stack.toString().take(MAX_STACK_CHARS),
                )
            }
            previous?.uncaughtException(thread, error)
        }
    }

    /** The process, named where the platform will say and numbered always. */
    private fun processTag(): String {
        val pid = android.os.Process.myPid()
        val name = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            runCatching { getProcessName() }.getOrNull()
        } else {
            null
        }
        return if (name != null) "$name (pid $pid)" else "pid $pid"
    }

    private companion object {
        /**
         * Enough for the frames that name the failure, not so much that one
         * crash fills the file a user is being asked to send.
         */
        const val MAX_STACK_CHARS = 4000
    }
}
