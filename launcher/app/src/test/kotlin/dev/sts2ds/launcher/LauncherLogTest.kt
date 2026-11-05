package dev.sts2ds.launcher

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class LauncherLogTest {
    private var wasVerbose = false
    private val seen = mutableListOf<String>()
    private val listener = LauncherLog.Listener { line, _ -> seen.add(line) }

    @Before fun setUp() {
        wasVerbose = LauncherLog.verbose
        LauncherLog.addListener(listener)
    }

    @After fun tearDown() {
        LauncherLog.removeListener(listener)
        LauncherLog.verbose = wasVerbose
    }

    @Test
    fun releaseStillShowsEveryLineOnTheStatusLine() {
        LauncherLog.verbose = false
        LauncherLog.log("routine")
        LauncherLog.warn("broken")
        assertEquals(2, seen.size)
        assertTrue(seen[0].endsWith("routine"))
        assertTrue(seen[1].endsWith("broken"))
    }

    @Test
    fun failureWithThrowableIsKept() {
        LauncherLog.verbose = false
        LauncherLog.log("Download failed", IllegalStateException("boom"))
        assertTrue(seen.single().endsWith("Download failed: java.lang.IllegalStateException: boom"))
    }
}
