package dev.sts2ds.launcher

import dev.sts2ds.LogLevel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class LauncherLogTest {
    private var wasLevel = LogLevel.WARN
    private val seen = mutableListOf<String>()
    private val listener = LauncherLog.Listener { line, _ -> seen.add(line) }

    @Before fun setUp() {
        wasLevel = LauncherLog.level
        LauncherLog.addListener(listener)
    }

    @After fun tearDown() {
        LauncherLog.removeListener(listener)
        LauncherLog.level = wasLevel
    }

    @Test
    fun releaseStillShowsEveryLineOnTheStatusLine() {
        LauncherLog.level = LogLevel.WARN
        LauncherLog.debug("detail")
        LauncherLog.log("routine")
        LauncherLog.warn("broken")
        assertEquals(3, seen.size)
        assertTrue(seen[0].endsWith("detail"))
        assertTrue(seen[1].endsWith("routine"))
        assertTrue(seen[2].endsWith("broken"))
    }

    @Test
    fun failureWithThrowableIsKept() {
        LauncherLog.level = LogLevel.WARN
        LauncherLog.log("Download failed", IllegalStateException("boom"))
        assertTrue(seen.single().endsWith("Download failed: java.lang.IllegalStateException: boom"))
    }

    @Test
    fun levelsIncludeTheOnesBefore() {
        assertTrue(LogLevel.WARN.allows(LogLevel.WARN))
        assertFalse(LogLevel.WARN.allows(LogLevel.INFO))
        assertTrue(LogLevel.INFO.allows(LogLevel.WARN))
        assertTrue(LogLevel.INFO.allows(LogLevel.INFO))
        assertFalse(LogLevel.INFO.allows(LogLevel.DEBUG))
        assertTrue(LogLevel.DEBUG.allows(LogLevel.WARN))
        assertTrue(LogLevel.DEBUG.allows(LogLevel.INFO))
        assertTrue(LogLevel.DEBUG.allows(LogLevel.DEBUG))
    }
}
