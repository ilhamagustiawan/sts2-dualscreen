package dev.sts2ds.launcher

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException

class LaunchFilesTest {
    @get:Rule val tmp = TemporaryFolder()

    private fun game(): File = tmp.newFolder().also {
        File(it, "SlayTheSpire2.pck").writeText("pck")
        File(it, "data/sts2.dll").apply { parentFile!!.mkdirs() }.writeText("dll")
    }

    private fun ready(dir: File) = LaunchFiles.requireReady(dir, "SlayTheSpire2.pck", "data")

    @Test
    fun completeGamePasses() {
        val dir = game()
        ready(dir)
        File(dir, ".download-started").writeText("1")
        File(dir, ".download-complete").writeText("1")
        ready(dir)
    }

    @Test(expected = IOException::class)
    fun interruptedDownloadIsRefused() {
        val dir = game()
        File(dir, ".download-started").writeText("1")
        ready(dir)
    }

    @Test(expected = IOException::class)
    fun emptyPckIsRefused() {
        val dir = game()
        File(dir, "SlayTheSpire2.pck").writeText("")
        ready(dir)
    }

    @Test
    fun stagedFilesMustAllBeThereAndNonEmpty() {
        val dir = tmp.newFolder()
        File(dir, "a.dll").writeText("a")
        File(dir, "b.dll").writeText("b")
        assertTrue(LaunchFiles.stagedFilesPresent(dir, listOf("a.dll", "b.dll")))
        assertFalse(LaunchFiles.stagedFilesPresent(dir, listOf("a.dll", "c.dll")))
        File(dir, "b.dll").writeText("")
        assertFalse(LaunchFiles.stagedFilesPresent(dir, listOf("a.dll", "b.dll")))
        assertTrue(LaunchFiles.stagedFilesPresent(dir, listOf("a.dll", "b.dll"), allowEmpty = true))
        assertFalse(LaunchFiles.stagedFilesPresent(dir, emptyList()))
    }
}
