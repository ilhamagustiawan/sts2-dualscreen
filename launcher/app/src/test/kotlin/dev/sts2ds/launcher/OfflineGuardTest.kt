package dev.sts2ds.launcher

import dev.sts2ds.launcher.OfflineGuard.Prompt
import dev.sts2ds.launcher.OfflineGuard.Verdict
import org.junit.Assert.assertEquals
import org.junit.Test

class OfflineGuardTest {
    private fun probe(status: String, summary: String? = null) =
        """{"Schema": 2, "Status": "$status", "Success": ${status == "ready"}""" +
            (if (summary != null) """, "FailureSummary": "$summary"""" else "") + "}"

    @Test
    fun readyProbeIsReady() {
        assertEquals(Verdict.Ready, OfflineGuard.verdict(probe("ready")))
    }

    @Test
    fun terminalFailuresCarryStatusAndSummary() {
        for (status in listOf("unsupported_api", "apply_failed", "runtime_failed")) {
            assertEquals(Verdict.Failed(status, "ModelDb.Init"), OfflineGuard.verdict(probe(status, "ModelDb.Init")))
        }
        assertEquals(Verdict.Failed("apply_failed", ""), OfflineGuard.verdict(probe("apply_failed")))
        // The probe serialises an unset summary as null.
        assertEquals(
            Verdict.Failed("apply_failed", ""),
            OfflineGuard.verdict("""{"Status": "apply_failed", "FailureSummary": null}"""),
        )
    }

    @Test
    fun unfinishedOrUnreadableProbeIsUnverified() {
        // A run that stopped mid-boot proves nothing either way.
        for (status in listOf("starting", "patches_installed", "modeldb_initializing")) {
            assertEquals(Verdict.Unverified, OfflineGuard.verdict(probe(status)))
        }
        assertEquals(Verdict.Unverified, OfflineGuard.verdict(null))
        assertEquals(Verdict.Unverified, OfflineGuard.verdict("not json"))
        assertEquals(Verdict.Unverified, OfflineGuard.verdict("{}"))
    }

    @Test
    fun promptFollowsVerdict() {
        assertEquals(Prompt.NONE, OfflineGuard.prompt(Verdict.Ready, OfflineGuard.Confirmation.NOT_YET))
        assertEquals(Prompt.CONFIRM, OfflineGuard.prompt(Verdict.Unverified, OfflineGuard.Confirmation.NOT_YET))
        assertEquals(Prompt.NONE, OfflineGuard.prompt(Verdict.Unverified, OfflineGuard.Confirmation.GIVEN))
        // A known failure is shown every time, confirmed or not.
        val failed = Verdict.Failed("apply_failed", "")
        assertEquals(Prompt.FAILED, OfflineGuard.prompt(failed, OfflineGuard.Confirmation.GIVEN))
    }

    @Test
    fun tupleNamesEveryPartThatChangesTheOutcome() {
        assertEquals(
            "pack/any/0.2.0/v0.112.0/abc",
            OfflineGuard.tuple("pack", "any", "0.2.0", "v0.112.0", "abc"),
        )
    }
}
