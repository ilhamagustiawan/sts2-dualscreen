package dev.sts2ds.launcher

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class GameSettingsFileTest {
    private fun playerSettings() = JSONObject()
        .put("fps_limit", 60)
        .put("msaa", 4)
        .put("vsync", "on")
        .put("preload_combat_animation_warmup_mode", "off")
        .put("some_future_key", "kept")

    @Test
    fun missingKeysAreFilledAndPlayerValuesKept() {
        val json = playerSettings()
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        assertEquals(4, json.getInt("msaa"))
        assertEquals("on", json.getString("vsync"))
        assertEquals("off", json.getString("preload_combat_animation_warmup_mode"))
        assertTrue(json.getBoolean("preload_enabled"))
        assertTrue(json.getBoolean("preload_combat_hit_effect_warmup_enabled"))
        assertEquals("kept", json.getString("some_future_key"))
        assertEquals(60, json.getInt("fps_limit"))
    }

    @Test
    fun firstTimeAppliesRecommendedGraphicsAndWarmups() {
        val json = playerSettings()
        GameSettingsFile.ensurePerformanceDefaults(json, firstTime = true)
        assertEquals(0, json.getInt("msaa"))
        assertEquals("off", json.getString("vsync"))
        for ((key, value) in GameSettingsFile.WARMUPS_ON) assertEquals(key, value, json.get(key))
        assertTrue(GameSettingsFile.warmupsEnabled(json))
        assertEquals("kept", json.getString("some_future_key"))
    }

    @Test
    fun secondRunChangesNothing() {
        val json = playerSettings()
        GameSettingsFile.ensurePerformanceDefaults(json, firstTime = true)
        assertFalse(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        // Round trip through text, as the file does.
        assertFalse(GameSettingsFile.ensurePerformanceDefaults(JSONObject(json.toString()), firstTime = false))
    }

    @Test
    fun animationWarmupIsTurnedBackOff() {
        val json = playerSettings().put(GameSettingsFile.KEY_ANIMATION_WARMUP, "safe")
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        assertEquals("off", json.getString(GameSettingsFile.KEY_ANIMATION_WARMUP))
        GameSettingsFile.setWarmups(json, true)
        assertEquals("off", json.getString(GameSettingsFile.KEY_ANIMATION_WARMUP))
        assertTrue(GameSettingsFile.warmupsEnabled(json))
    }

    @Test
    fun renderSizeIsAVector() {
        val json = JSONObject()
        assertEquals(0 to 0, GameSettingsFile.renderSize(json))
        GameSettingsFile.putRenderSize(json, 1280, 720)
        assertEquals("""{"X":1280,"Y":720}""", json.getJSONObject("fullscreen_render_size").toString())
        assertEquals(1280 to 720, GameSettingsFile.renderSize(JSONObject(json.toString())))
    }

    @Test
    fun warmupSwitchSetsEveryWarmupKey() {
        val json = JSONObject()
        GameSettingsFile.setWarmups(json, true)
        for ((key, value) in GameSettingsFile.WARMUPS_ON) assertEquals(key, value, json.get(key))
        GameSettingsFile.setWarmups(json, false)
        for ((key, value) in GameSettingsFile.WARMUPS_OFF) assertEquals(key, value, json.get(key))
        assertFalse(GameSettingsFile.warmupsEnabled(json))
    }

    @Test
    fun inGameRefreshToggle() {
        val high = SettingsStore.REFRESH_HIGH
        val hz60 = SettingsStore.REFRESH_60HZ
        val system = SettingsStore.REFRESH_SYSTEM
        assertEquals(hz60, GameSettingsFile.refreshAfterGame(hz60, null))
        assertEquals(high, GameSettingsFile.refreshAfterGame(hz60, true))
        assertEquals(hz60, GameSettingsFile.refreshAfterGame(hz60, false))
        assertEquals(system, GameSettingsFile.refreshAfterGame(high, false))
        assertEquals(high, GameSettingsFile.refreshAfterGame(system, true))
    }
}
