package dev.sts2ds.launcher

import org.junit.Assert.assertEquals
import org.junit.Test

class BranchDefaultTest {
    @Test
    fun freshInstallGetsBeta() {
        assertEquals(SettingsStore.BRANCH_BETA, SettingsStore.initialBranch(null))
    }

    @Test
    fun installedStableStaysStable() {
        assertEquals(SettingsStore.BRANCH_STABLE, SettingsStore.initialBranch(SettingsStore.BRANCH_STABLE))
    }
}
