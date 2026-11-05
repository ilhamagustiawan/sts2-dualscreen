package dev.sts2ds.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class GcParamsTest {
    @Test
    fun nurserySizesArePowersOfTwoWithConcurrentMajor() {
        assertEquals("nursery-size=16m,major=marksweep-conc", GamePrep.gcParams(SettingsStore.GC_16M))
        assertEquals("nursery-size=32m,major=marksweep-conc", GamePrep.gcParams(SettingsStore.GC_32M))
        assertEquals("nursery-size=64m,major=marksweep-conc", GamePrep.gcParams(SettingsStore.GC_64M))
        assertEquals("nursery-size=256m,major=marksweep-conc", GamePrep.gcParams(SettingsStore.GC_256M))
    }

    @Test
    fun defaultLeavesTheRuntimeAlone() {
        assertNull(GamePrep.gcParams(SettingsStore.GC_DEFAULT))
        assertNull(GamePrep.gcParams("something else"))
    }
}
