// SettingsActivity — the launcher's settings screen.
//
// Laid out like SilksongAndroid's (MIT): every toggle commits as it is
// flipped, with a note under each saying what it does. Dual-screen settings
// reach the game through GamePrep, which writes dualscreen.json right before
// each launch, so changes apply on the next launch.

package dev.sts2ds.launcher

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import dev.sts2ds.R

class SettingsActivity : Activity() {

    private lateinit var settings: SettingsStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        settings = SettingsStore(this)

        bind(R.id.sw_dual_screen, settings.dualScreen) {
            settings.dualScreen = it
            LauncherLog.log("Settings: dual screen → $it")
        }
        bind(R.id.sw_dual_screen_60, settings.dualScreenFps >= 60) {
            settings.dualScreenFps = if (it) 60 else 30
            LauncherLog.log("Settings: lower screen ${settings.dualScreenFps} FPS")
        }
        bind(R.id.sw_top_hint, settings.dualScreenTopHint) {
            settings.dualScreenTopHint = it
        }
        bind(R.id.sw_auto_pull, settings.autoPull) {
            settings.autoPull = it
            LauncherLog.log("Settings: auto-pull → $it")
        }
        bind(R.id.sw_auto_push, settings.autoPush) {
            settings.autoPush = it
            LauncherLog.log("Settings: auto-push → $it")
        }

        val tabButton: Button = findViewById(R.id.btn_default_tab)
        fun showTab() {
            tabButton.text = getString(R.string.settings_default_tab, TAB_NAMES[settings.dualScreenDefaultTab] ?: "Map")
        }
        showTab()
        tabButton.setOnClickListener {
            val keys = TAB_NAMES.keys.toList()
            val next = keys[(keys.indexOf(settings.dualScreenDefaultTab) + 1).mod(keys.size)]
            settings.dualScreenDefaultTab = next
            showTab()
        }

        bindPerformance()

        findViewById<Button>(R.id.btn_reset).setOnClickListener { confirmReset() }
        findViewById<Button>(R.id.btn_settings_logs).setOnClickListener {
            startActivity(Intent(this, LogActivity::class.java))
        }
        findViewById<Button>(R.id.btn_settings_back).setOnClickListener { finish() }
    }

    // Most of these are the compat pack's keys in the game's settings.save
    // (GameSettingsFile), written straight there; the renderer and refresh rate
    // are the launcher's own and reach the game through GamePrep.
    private fun bindPerformance() {
        val game = GameSettingsFile(this)
        val json = game.read()
        fun edit(what: String, change: (org.json.JSONObject) -> Unit) {
            try {
                game.update { change(it); true }
                LauncherLog.log("Settings: $what")
            } catch (e: Exception) {
                Toast.makeText(this, e.message ?: e.javaClass.simpleName, Toast.LENGTH_LONG).show()
            }
        }

        if (json == null) findViewById<TextView>(R.id.txt_perf_note).setText(R.string.settings_perf_note_missing)
        for (id in GAME_FILE_CONTROLS) findViewById<View>(id).isEnabled = json != null
        val settingsJson = json ?: org.json.JSONObject()

        bind(R.id.sw_warmups, GameSettingsFile.warmupsEnabled(settingsJson)) { on ->
            edit("combat warmups → $on") { GameSettingsFile.setWarmups(it, on) }
        }
        bind(R.id.sw_preload, settingsJson.optBoolean(GameSettingsFile.KEY_PRELOAD, true)) { on ->
            edit("preload → $on") { it.put(GameSettingsFile.KEY_PRELOAD, on) }
        }
        bind(R.id.sw_shader_compat, settingsJson.optBoolean(GameSettingsFile.KEY_SHADER_COMPAT, false)) { on ->
            edit("cheaper shaders → $on") { it.put(GameSettingsFile.KEY_SHADER_COMPAT, on) }
        }
        bind(R.id.sw_overlay, settingsJson.optBoolean(GameSettingsFile.KEY_OVERLAY, false)) { on ->
            edit("performance overlay → $on") { it.put(GameSettingsFile.KEY_OVERLAY, on) }
        }

        cycle(
            R.id.btn_render_size, R.string.settings_render_size, GameSettingsFile.RENDER_SIZES,
            GameSettingsFile.renderSize(settingsJson),
            { (w, h) -> if (w > 0) "$w × $h" else getString(R.string.settings_render_size_native) },
        ) { (w, h) -> edit("render size → ${w}x$h") { GameSettingsFile.putRenderSize(it, w, h) } }
        cycle(
            R.id.btn_msaa, R.string.settings_msaa, GameSettingsFile.MSAA_LEVELS,
            settingsJson.optInt(GameSettingsFile.KEY_MSAA, 0),
            { if (it > 0) "${it}×" else getString(R.string.settings_msaa_off) },
        ) { level -> edit("msaa → $level") { it.put(GameSettingsFile.KEY_MSAA, level) } }
        cycle(
            R.id.btn_vsync, R.string.settings_vsync, GameSettingsFile.VSYNC_MODES,
            settingsJson.optString(GameSettingsFile.KEY_VSYNC, "off"),
            { it.replaceFirstChar(Char::uppercase) },
        ) { mode -> edit("vsync → $mode") { it.put(GameSettingsFile.KEY_VSYNC, mode) } }

        cycle(
            R.id.btn_refresh_rate, R.string.settings_refresh_rate, REFRESH_NAMES.keys.toList(),
            settings.refreshRate, { getString(REFRESH_NAMES.getValue(it)) },
        ) { mode ->
            settings.refreshRate = mode
            LauncherLog.log("Settings: refresh rate → $mode")
            // Keep the compat pack's in-game toggle in step, or GamePrep would
            // read the old value back as an in-game change.
            if (game.exists) edit("in-game high refresh → ${mode == SettingsStore.REFRESH_HIGH}") {
                it.put(GameSettingsFile.KEY_HIGH_REFRESH, mode == SettingsStore.REFRESH_HIGH)
            }
        }
        bind(R.id.sw_opengl, settings.renderer == SettingsStore.RENDERER_OPENGL) { on ->
            settings.renderer = if (on) SettingsStore.RENDERER_OPENGL else SettingsStore.RENDERER_VULKAN
            LauncherLog.log("Settings: renderer → ${settings.renderer}")
        }
    }

    /** A button that steps through [values], showing the current one in [label]. */
    private fun <T> cycle(id: Int, label: Int, values: List<T>, initial: T, name: (T) -> String, onChange: (T) -> Unit) {
        val button: Button = findViewById(id)
        // An unlisted value (set in-game) shows as itself; a tap moves to the first.
        var index = values.indexOf(initial)
        button.text = getString(label, name(initial))
        button.setOnClickListener {
            index = (index + 1).mod(values.size)
            button.text = getString(label, name(values[index]))
            onChange(values[index])
        }
    }

    // Listener set after the initial state so seeding it is not a write.
    private fun bind(id: Int, initial: Boolean, onChange: (Boolean) -> Unit) {
        val sw: Switch = findViewById(id)
        sw.isChecked = initial
        sw.setOnCheckedChangeListener { _, checked -> onChange(checked) }
    }

    private fun confirmReset() {
        AlertDialog.Builder(this)
            .setTitle(R.string.settings_reset)
            .setMessage(R.string.settings_reset_subtitle)
            .setNegativeButton(android.R.string.cancel, null)
            .setPositiveButton(R.string.settings_reset) { _, _ ->
                GameReset.clear(this)
                Toast.makeText(this, R.string.settings_reset_done, Toast.LENGTH_LONG).show()
            }
            .show()
    }

    private companion object {
        val GAME_FILE_CONTROLS = intArrayOf(
            R.id.sw_warmups, R.id.sw_preload, R.id.sw_shader_compat, R.id.sw_overlay,
            R.id.btn_render_size, R.id.btn_msaa, R.id.btn_vsync,
        )
        val REFRESH_NAMES = linkedMapOf(
            SettingsStore.REFRESH_SYSTEM to R.string.settings_refresh_system,
            SettingsStore.REFRESH_60HZ to R.string.settings_refresh_60,
            SettingsStore.REFRESH_HIGH to R.string.settings_refresh_high,
        )
        val TAB_NAMES = linkedMapOf(
            "map" to "Map",
            "draw" to "Draw pile",
            "discard" to "Discard pile",
            "exhaust" to "Exhaust pile",
            "deck" to "Deck",
        )
    }
}
