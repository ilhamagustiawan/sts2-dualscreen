package dev.sts2ds.launcher

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CaptureSwitchesTest {
    @Test
    fun switchesGoUnderDsConfigKeys() {
        val json = GamePrep.putCaptureSwitches(JSONObject().put("fps", 30), presentThread = true, stagger = false, pauseHidden = true)
        assertTrue(json.getBoolean("present_thread"))
        assertFalse(json.getBoolean("stagger_captures"))
        assertTrue(json.getBoolean("pause_hidden_pages"))
        assertEquals(30, json.getInt("fps"))
    }

    @Test
    fun allOffIsWrittenAsFalse() {
        val json = GamePrep.putCaptureSwitches(JSONObject(), presentThread = false, stagger = false, pauseHidden = false)
        assertFalse(json.getBoolean("present_thread"))
        assertFalse(json.getBoolean("stagger_captures"))
        assertFalse(json.getBoolean("pause_hidden_pages"))
    }
}
