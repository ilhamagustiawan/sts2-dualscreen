package dev.sts2ds.launcher

import java.io.File
import java.io.IOException

/** Cheap preflight checks; these do not claim to verify the depot's checksums. */
internal object LaunchFiles {
    fun requireReady(gameDir: File, pckName: String, assemblyDir: String) {
        if (File(gameDir, ".download-started").exists() && !File(gameDir, ".download-complete").isFile) {
            throw IOException("The game download was interrupted. Resume Download game before launching.")
        }
        for (path in listOf(pckName, "$assemblyDir/sts2.dll")) {
            val file = File(gameDir, path)
            if (!file.isFile || file.length() == 0L) {
                throw IOException("Game file $path is missing or empty. Download the game again before launching.")
            }
        }
    }

    /** Cached staging is usable only while every recorded file is still present. */
    fun stagedFilesPresent(dir: File, names: Collection<String>, allowEmpty: Boolean = false): Boolean =
        names.isNotEmpty() && names.all { name ->
            val file = File(dir, name)
            file.isFile && (allowEmpty || file.length() > 0L)
        }
}
