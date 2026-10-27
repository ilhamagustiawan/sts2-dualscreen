// Sts2App — the Application class: runs once per process, before any activity.
//
// Adapted from SilksongAndroid's SilksongApp (MIT). Its Unity dex injection is
// gone; what is left is the launcher log, a crash handler that records
// uncaught exceptions from every process, and BouncyCastle for JavaSteam.

package dev.sts2ds.launcher

import android.app.Application
import android.os.Build

class Sts2App : Application() {

    // Before any class in this app is loaded, which rules out onCreate.
    //
    // The APK links against com.unity3d.player.* and ships none of it: the
    // player classes are dexed on the device out of the module the app
    // downloads. GameActivity's superclass is one of the types that resolves
    // from there, so the dex has to be in the class loader before the
    // framework instantiates the activity. attachBaseContext is the first
    // point in the process where that is possible.
    override fun onCreate() {
        super.onCreate()

        // First, so that everything below is recorded too. The log is written
        // to a file from here on, which is what makes it survivable enough to
        // be worth asking a user for. It also drains whatever
        // attachBaseContext logged above, which had nowhere to go yet.
        LauncherLog.attach(this)

        // Which process this is. The log is shared by all of them and every
        // session in it opens with the same two lines, so until now a header
        // could not be told from :launcher, :builder or the game -- and
        // "the game's process started and then nothing" was not a readable
        // fact. It is the first thing worth knowing about a launch that
        // failed. (Issue #24.)
        LauncherLog.log("process ${processTag()} started")

        // Before the engine's own handler, which cannot be installed until
        // GameActivity exists -- see installCrashHandler.
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
                LauncherLog.log(
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
