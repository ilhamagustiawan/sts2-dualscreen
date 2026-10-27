// SettingsActivity — the launcher's settings screen.
//
// Laid out like SilksongAndroid's (MIT): every toggle commits as it is
// flipped, with a note under each saying what it does. The page opens on the
// few settings most people want; graphics and tuning wait under Advanced.
// Dual-screen settings reach the game through GamePrep, which writes
// dualscreen.json right before each launch, so changes apply on the next launch.

package dev.sts2ds.launcher

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.Spinner
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
        bindBranch()
        bind(R.id.sw_dual_screen_60, settings.dualScreenFps >= 60) {
            settings.dualScreenFps = if (it) 60 else 30
            LauncherLog.log("Settings: lower screen ${settings.dualScreenFps} FPS")
        }
        bind(R.id.sw_top_hint, settings.dualScreenTopHint) {
            settings.dualScreenTopHint = it
        }
        // One switch for both directions; they were rarely wanted apart.
        bind(R.id.sw_cloud_sync, settings.autoPull && settings.autoPush) {
            settings.autoPull = it
            settings.autoPush = it
            LauncherLog.log("Settings: cloud sync → $it")
        }
        choose(
            R.id.sp_default_tab, TAB_NAMES.keys.toList(), settings.dualScreenDefaultTab,
            { TAB_NAMES[it] ?: it },
        ) { settings.dualScreenDefaultTab = it }

        bindPerformance()

        val advanced: View = findViewById(R.id.advanced_rows)
        val advancedButton: Button = findViewById(R.id.btn_advanced)
        fun showAdvanced(open: Boolean) {
            advanced.visibility = if (open) View.VISIBLE else View.GONE
            advancedButton.setText(if (open) R.string.settings_advanced_hide else R.string.settings_advanced_show)
        }
        showAdvanced(false)
        advancedButton.setOnClickListener { showAdvanced(advanced.visibility != View.VISIBLE) }

        findViewById<Button>(R.id.btn_reset).setOnClickListener { confirmReset() }
        findViewById<Button>(R.id.btn_settings_logs).setOnClickListener {
            startActivity(Intent(this, LogActivity::class.java))
        }
        findViewById<Button>(R.id.btn_settings_back).setOnClickListener { finish() }
    }

    // The download reads the branch; the launcher offers it as the next
    // download once it differs from the installed one. Not while a download
    // runs, which has already picked its branch.
    private fun bindBranch() {
        val busy = DownloadState.isActive
        findViewById<Spinner>(R.id.sp_game_branch).isEnabled = !busy
        if (busy) findViewById<TextView>(R.id.txt_branch_note).setText(R.string.settings_game_branch_busy)
        choose(
            R.id.sp_game_branch, listOf(SettingsStore.BRANCH_STABLE, SettingsStore.BRANCH_BETA), settings.gameBranch,
            { SettingsStore.branchName(this, it) },
        ) {
            settings.gameBranch = it
            LauncherLog.log("Settings: game branch → $it")
        }
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

        choose(
            R.id.sp_render_size, GameSettingsFile.RENDER_SIZES, GameSettingsFile.renderSize(settingsJson),
            { (w, h) -> if (w > 0) "$w × $h" else getString(R.string.settings_render_size_native) },
        ) { (w, h) -> edit("render size → ${w}x$h") { GameSettingsFile.putRenderSize(it, w, h) } }
        choose(
            R.id.sp_msaa, GameSettingsFile.MSAA_LEVELS, settingsJson.optInt(GameSettingsFile.KEY_MSAA, 0),
            { if (it > 0) "${it}×" else getString(R.string.settings_msaa_off) },
        ) { level -> edit("msaa → $level") { it.put(GameSettingsFile.KEY_MSAA, level) } }
        choose(
            R.id.sp_vsync, GameSettingsFile.VSYNC_MODES, settingsJson.optString(GameSettingsFile.KEY_VSYNC, "on"),
            { it.replaceFirstChar(Char::uppercase) },
        ) { mode -> edit("vsync → $mode") { it.put(GameSettingsFile.KEY_VSYNC, mode) } }

        choose(
            R.id.sp_refresh_rate, REFRESH_NAMES.keys.toList(), settings.refreshRate,
            { getString(REFRESH_NAMES.getValue(it)) },
        ) { mode ->
            settings.refreshRate = mode
            LauncherLog.log("Settings: refresh rate → $mode")
            // Keep the compat pack's in-game toggle in step, or GamePrep would
            // read the old value back as an in-game change.
            if (game.exists) edit("in-game high refresh → ${mode == SettingsStore.REFRESH_HIGH}") {
                it.put(GameSettingsFile.KEY_HIGH_REFRESH, mode == SettingsStore.REFRESH_HIGH)
            }
        }
        // Not a settings.save key: the dual-screen layer thins the emitters.
        choose(
            R.id.sp_particles, PARTICLE_AMOUNTS.keys.toList(), settings.particleAmount,
            { PARTICLE_AMOUNTS[it]?.let(::getString) ?: "$it%" },
        ) { percent ->
            settings.particleAmount = percent
            LauncherLog.log("Settings: particles → $percent%")
        }
        bind(R.id.sw_opengl, settings.renderer == SettingsStore.RENDERER_OPENGL) { on ->
            settings.renderer = if (on) SettingsStore.RENDERER_OPENGL else SettingsStore.RENDERER_VULKAN
            LauncherLog.log("Settings: renderer → ${settings.renderer}")
        }
    }

    /** A dropdown of [values], named by [name], starting on [initial]. */
    private fun <T> choose(id: Int, values: List<T>, initial: T, name: (T) -> String, onChange: (T) -> Unit) {
        val spinner: Spinner = findViewById(id)
        // An unlisted value (set in-game) is offered as itself, first.
        val options = if (initial in values) values else listOf(initial) + values
        spinner.adapter = ArrayAdapter(this, R.layout.item_choice, options.map(name)).apply {
            setDropDownViewResource(R.layout.item_choice_dropdown)
        }
        var current = options.indexOf(initial)
        spinner.setSelection(current, false)
        // A spinner reports its first layout as a selection: only real changes count.
        spinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                if (position == current) return
                current = position
                onChange(options[position])
            }

            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
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
            R.id.sp_render_size, R.id.sp_msaa, R.id.sp_vsync,
        )
        val REFRESH_NAMES = linkedMapOf(
            SettingsStore.REFRESH_SYSTEM to R.string.settings_refresh_system,
            SettingsStore.REFRESH_60HZ to R.string.settings_refresh_60,
            SettingsStore.REFRESH_HIGH to R.string.settings_refresh_high,
        )
        val PARTICLE_AMOUNTS = linkedMapOf(
            100 to R.string.settings_particles_full,
            50 to R.string.settings_particles_half,
            25 to R.string.settings_particles_low,
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
