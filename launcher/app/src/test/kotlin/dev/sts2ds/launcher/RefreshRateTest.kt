package dev.sts2ds.launcher

import com.godot.game.RefreshRate
import org.junit.Assert.assertEquals
import org.junit.Test

class RefreshRateTest {
    // The AYN Thor's top panel: portrait 1080x1920 at 60 and 120 Hz.
    private val widths = intArrayOf(1080, 1080)
    private val heights = intArrayOf(1920, 1920)
    private val rates = floatArrayOf(60.000004f, 120.00001f)

    private fun choose(mode: String, w: Int = 1080, h: Int = 1920) =
        RefreshRate.choose(widths, heights, rates, w, h, mode)

    @Test
    fun sixtyPicksTheSixtyHertzMode() = assertEquals(0, choose(RefreshRate.HZ60))

    @Test
    fun highPicksTheFastestMode() = assertEquals(1, choose(RefreshRate.HIGH))

    @Test
    fun systemRequestsNothing() = assertEquals(-1, choose(RefreshRate.SYSTEM))

    @Test
    fun otherSizesAreIgnored() = assertEquals(-1, choose(RefreshRate.HIGH, w = 1440, h = 2560))

    @Test
    fun highNeedsMoreThanSixty() {
        assertEquals(-1, RefreshRate.choose(intArrayOf(1080), intArrayOf(1920), floatArrayOf(60f), 1080, 1920, RefreshRate.HIGH))
    }
}
