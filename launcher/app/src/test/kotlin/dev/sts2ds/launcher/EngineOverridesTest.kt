package dev.sts2ds.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class EngineOverridesTest {
    @get:Rule val tmp = TemporaryFolder()

    @Test
    fun pipelineChunkGoesUnderRendering() {
        val text = EngineOverrides.render(savePipelinesOften = true)!!
        val lines = text.lines()
        val section = lines.indexOf("[rendering]")
        assertTrue(section > 0)
        assertTrue(lines.indexOf("rendering_device/pipeline_cache/save_chunk_size_mb=0.25") > section)
    }

    @Test
    fun nothingToOverrideMeansNoFile() {
        assertNull(EngineOverrides.render(savePipelinesOften = false))
        assertNull(EngineOverrides.render(savePipelinesOften = false, framePacing = EngineOverrides.PACING_STEADY))
    }

    @Test
    fun framePacingGoesUnderDisplay() {
        val adaptive = EngineOverrides.render(false, EngineOverrides.PACING_ADAPTIVE)!!.lines()
        assertTrue(adaptive.indexOf("window/frame_pacing/android/swappy_mode=1") > adaptive.indexOf("[display]"))
        val off = EngineOverrides.render(true, EngineOverrides.PACING_OFF)!!.lines()
        assertTrue(off.indexOf("window/frame_pacing/android/enable_frame_pacing=false") > off.indexOf("[display]"))
        assertTrue(off.indexOf("[rendering]") > off.indexOf("[display]"))
    }

    @Test
    fun applyWritesRewritesAndRemovesOnlyItsOwnFile() {
        val dir = tmp.newFolder()
        val file = File(dir, EngineOverrides.FILE_NAME)
        val text = EngineOverrides.render(true)
        EngineOverrides.apply(dir, text)
        assertEquals(text, file.readText())
        EngineOverrides.apply(dir, null)
        assertFalse(file.exists())

        file.writeText("[display]\nwindow/size/viewport_width=1\n")
        EngineOverrides.apply(dir, text)
        EngineOverrides.apply(dir, null)
        assertEquals("[display]\nwindow/size/viewport_width=1\n", file.readText())
    }
}
