package dev.sts2ds.launcher

import dev.sts2ds.launcher.SyncPlan.Action
import org.junit.Assert.assertEquals
import org.junit.Test

class SyncPlanTest {
    @Test
    fun nothingNewerAnywhere() {
        assertEquals(Action.NOTHING, SyncPlan.decide(newerOnCloud = 0, newerOnDevice = 0))
    }

    @Test
    fun oneSideNewerMovesWithoutAsking() {
        assertEquals(Action.DOWNLOAD, SyncPlan.decide(newerOnCloud = 3, newerOnDevice = 0))
        assertEquals(Action.UPLOAD, SyncPlan.decide(newerOnCloud = 0, newerOnDevice = 2))
    }

    @Test
    fun bothSidesNewerAsks() {
        assertEquals(Action.ASK, SyncPlan.decide(newerOnCloud = 1, newerOnDevice = 1))
    }

    @Test
    fun unsyncedPlayCountsAsADeviceChange() {
        // A finished run deletes its save, which no mtime shows.
        assertEquals(Action.ASK, SyncPlan.decide(newerOnCloud = 1, newerOnDevice = 0, deviceHasUnsyncedPlay = true))
        assertEquals(Action.NOTHING, SyncPlan.decide(newerOnCloud = 0, newerOnDevice = 0, deviceHasUnsyncedPlay = true))
    }
}
