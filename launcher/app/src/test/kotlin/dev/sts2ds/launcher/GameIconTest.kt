package dev.sts2ds.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class GameIconTest {
    private val hash = "0123456789abcdef0123456789abcdef01234567"

    @Test
    fun sha1HashMapsToCdnIcon() {
        assertEquals(
            "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/2868840/$hash.ico",
            GameIcon.iconUrl(2868840, hash),
        )
    }

    @Test
    fun anythingElseIsNoIcon() {
        // Missing, truncated, upper-case, or trying to leave the path.
        for (bad in listOf(null, "", hash.dropLast(1), hash.uppercase(), "../$hash")) {
            assertNull(bad, GameIcon.iconUrl(2868840, bad))
        }
    }
}
