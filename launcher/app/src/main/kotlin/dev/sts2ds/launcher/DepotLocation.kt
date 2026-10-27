// DepotLocation — where the game's files live on the device.
//
// A Steam download always goes into the app's own external files directory:
// the resume path deletes files it did not write (see DepotFetcher), which is
// only safe inside a directory that holds nothing else. The files stay there;
// GamePrep reads them in place and the engine loads the PCK from there.

package dev.sts2ds.launcher

import android.content.Context
import java.io.File

object DepotLocation {
    /** Where the depot is downloaded to and read from. */
    fun gameDir(context: Context): File =
        File(context.getExternalFilesDir(null) ?: context.filesDir, "depot")

    /** Scratch space for the downloader's chunk staging. */
    fun stagingDir(context: Context): File =
        File(context.getExternalFilesDir(null) ?: context.cacheDir, "depot-staging")

    /** True when a complete download is on the device. */
    fun present(context: Context): Boolean = DepotFetcher.isPresent(gameDir(context))

    /** The Steam branch of the installed game, or null when none is installed. */
    fun installedBranch(context: Context): String? =
        if (present(context)) DepotFetcher.installedBranch(gameDir(context)) else null

    /** A game is installed, but from another branch than the one chosen in settings. */
    fun branchMismatch(context: Context): Boolean =
        installedBranch(context).let { it != null && it != SettingsStore(context).gameBranch }

    /** True when a download was started and never finished. */
    fun interrupted(context: Context): Boolean = DepotFetcher.isInterrupted(gameDir(context))
}
