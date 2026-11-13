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
        .put("preload_combat_animation_warmup_mode", "safe")
        .put("some_future_key", "kept")

    @Test
    fun missingKeysAreFilledAndPlayerValuesKept() {
        val json = playerSettings()
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        assertEquals(4, json.getInt("msaa"))
        assertEquals("on", json.getString("vsync"))
        assertTrue(json.getBoolean("preload_enabled"))
        assertEquals("kept", json.getString("some_future_key"))
        assertEquals(60, json.getInt("fps_limit"))
    }

    @Test
    fun firstTimeAppliesBalancedPresetAndWarmups() {
        val json = playerSettings()
            .put("fps_limit", 120)
            .put("vsync", "on")
            .put("skip_intro_logo", false)
            .put("preload_gameplay_assets_enabled", true)
        GameSettingsFile.putRenderSize(json, 1280, 720)
        GameSettingsFile.ensurePerformanceDefaults(json, firstTime = true)
        assertEquals(0, json.getInt("msaa"))
        assertEquals("off", json.getString("vsync"))
        assertEquals(60, json.getInt("fps_limit"))
        assertTrue(json.getBoolean("skip_intro_logo"))
        assertEquals(0 to 0, GameSettingsFile.renderSize(json))
        assertFalse(json.getBoolean("preload_gameplay_assets_enabled"))
        assertTrue(GameSettingsFile.warmupsEnabled(json))
        assertEquals("kept", json.getString("some_future_key"))
    }

    @Test
    fun laterRunsKeepPlayerGraphics() {
        val json = playerSettings()
        GameSettingsFile.ensurePerformanceDefaults(json, firstTime = true)
        GameSettingsFile.putRenderSize(json, 1600, 900)
        json.put("vsync", "on").put("skip_intro_logo", false)
        assertFalse(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        assertEquals(1600 to 900, GameSettingsFile.renderSize(json))
        assertEquals("on", json.getString("vsync"))
        assertFalse(json.getBoolean("skip_intro_logo"))
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
    fun animationAndHitEffectWarmupsAreForcedOff() {
        // An older launcher turned both on.
        val json = playerSettings().put(GameSettingsFile.KEY_HIT_EFFECT_WARMUP, true)
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        assertEquals("off", json.getString(GameSettingsFile.KEY_ANIMATION_WARMUP))
        assertFalse(json.getBoolean(GameSettingsFile.KEY_HIT_EFFECT_WARMUP))

        GameSettingsFile.setWarmups(json, true)
        assertEquals("off", json.getString(GameSettingsFile.KEY_ANIMATION_WARMUP))
        assertFalse(json.getBoolean(GameSettingsFile.KEY_HIT_EFFECT_WARMUP))
        assertTrue(GameSettingsFile.warmupsEnabled(json))
    }

    @Test
    fun shaderCompatibilityIsForcedOff() {
        // Turned on by an older launcher's switch, or the compat pack's in-game one.
        val json = playerSettings().put(GameSettingsFile.KEY_SHADER_COMPAT, true)
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false))
        assertFalse(json.getBoolean(GameSettingsFile.KEY_SHADER_COMPAT))
    }

    @Test
    fun combatCodeWarmupIsOnByDefault() {
        val json = playerSettings()
        GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false)
        assertTrue(json.getBoolean(GameSettingsFile.KEY_COMBAT_CODE_WARMUP))
        // An older launcher wrote false into the file; the default turns it on.
        val old = playerSettings().put(GameSettingsFile.KEY_COMBAT_CODE_WARMUP, false)
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(old, firstTime = false))
        assertTrue(old.getBoolean(GameSettingsFile.KEY_COMBAT_CODE_WARMUP))
        // The animation warmup stays off: they are separate settings.
        assertEquals("off", old.getString(GameSettingsFile.KEY_ANIMATION_WARMUP))
    }

    @Test
    fun combatCodeWarmupFollowsTheLauncherSwitch() {
        val json = playerSettings()
        GameSettingsFile.ensurePerformanceDefaults(json, firstTime = true, combatCode = false)
        assertFalse(json.getBoolean(GameSettingsFile.KEY_COMBAT_CODE_WARMUP))
        assertFalse(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false, combatCode = false))
        assertTrue(GameSettingsFile.ensurePerformanceDefaults(json, firstTime = false, combatCode = true))
        assertTrue(json.getBoolean(GameSettingsFile.KEY_COMBAT_CODE_WARMUP))
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
