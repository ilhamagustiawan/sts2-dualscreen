// EngineOverrides — engine project settings the launcher changes at boot.
//
// Godot 4.5.1 reads an override.cfg next to the --main-pack PCK right after
// the pack's own project settings (ProjectSettings::_setup, "Load override
// from location of the main pack"), before the renderer starts. That is the
// only way to change settings the engine reads once at startup, without
// touching the game's PCK. The file is ours: written with a marker line,
// rewritten only when it changes, and removed when nothing needs overriding.

package dev.sts2ds.launcher

import java.io.File

object EngineOverrides {
    const val FILE_NAME = "override.cfg"
    private const val MARKER = "; sts2-dualscreen launcher settings; rewritten on every launch"

    /**
     * Godot saves the Vulkan pipeline cache only after it has grown by this
     * many MB (engine default 3) or on a clean exit, which Android rarely
     * gives a game: it is usually killed in the background. Every 256 KB, the
     * pipelines a session compiled reach disk within moments; the save runs on
     * a worker thread and is skipped while one is in flight. A truncated file
     * is refused by the engine's header check and just rebuilt.
     */
    const val PIPELINE_SAVE_CHUNK_MB = 0.25

    /** override.cfg's text for these settings, or null when nothing differs from the engine. */
    fun render(savePipelinesOften: Boolean): String? {
        val rendering = mutableListOf<String>()
        if (savePipelinesOften) rendering += "rendering_device/pipeline_cache/save_chunk_size_mb=$PIPELINE_SAVE_CHUNK_MB"
        if (rendering.isEmpty()) return null
        return buildString {
            append(MARKER).append('\n')
            append("\n[rendering]\n\n")
            rendering.forEach { append(it).append('\n') }
        }
    }

    /** Writes or removes [dir]/override.cfg; leaves a file it did not write alone. */
    fun apply(dir: File, text: String?) {
        val file = File(dir, FILE_NAME)
        val existing = if (file.isFile) file.readText() else null
        if (existing != null && !existing.startsWith(MARKER)) {
            LauncherLog.log("$FILE_NAME in the game folder is not ours; leaving it as it is")
            return
        }
        if (text == null) {
            if (existing != null) file.delete()
            return
        }
        if (existing == text) return
        val tmp = File(dir, ".$FILE_NAME.tmp")
        tmp.writeText(text)
        if (!tmp.renameTo(file)) {
            tmp.delete()
            throw java.io.IOException("could not write $file")
        }
    }
}
