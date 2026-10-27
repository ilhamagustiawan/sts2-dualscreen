package dev.sts2ds.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class Sts2SavePathsTest {
    @Test
    fun bareRelativeNames() {
        assertEquals("profile1/saves/progress.save", Sts2SavePaths.relative("profile1/saves/progress.save"))
        assertEquals("profile.save", Sts2SavePaths.relative("profile.save"))
        assertEquals("modded/profile2/saves/prefs.save", Sts2SavePaths.relative("modded/profile2/saves/prefs.save"))
        assertEquals("profile3/saves/history/1234.run", Sts2SavePaths.relative("profile3/saves/history/1234.run"))
    }

    @Test
    fun prefixedAndWindowsNames() {
        val name = "user://steam/76561198000000000/profile1/saves/current_run.save"
        assertEquals("profile1/saves/current_run.save", Sts2SavePaths.relative(name))
        assertEquals("steam/76561198000000000/", Sts2SavePaths.prefix(name))
        assertEquals("profile2/saves/prefs.save", Sts2SavePaths.relative("%WinAppDataRoaming%SlayTheSpire2\\steam\\1\\profile2\\saves\\prefs.save"))
    }

    @Test
    fun notSaves() {
        assertNull(Sts2SavePaths.relative("settings.save"))
        assertNull(Sts2SavePaths.relative("profile1/saves/progress.save.tmp"))
        assertNull(Sts2SavePaths.relative("logs/godot.log"))
        assertNull(Sts2SavePaths.relative("profile9x/saves/a.save"))
    }

    @Test
    fun runSaves() {
        assertTrue(Sts2SavePaths.isRunSave("profile1/saves/current_run.save"))
        assertTrue(Sts2SavePaths.isRunSave("profile1/saves/current_run_mp.save.backup"))
        assertFalse(Sts2SavePaths.isRunSave("profile1/saves/progress.save"))
    }
}
