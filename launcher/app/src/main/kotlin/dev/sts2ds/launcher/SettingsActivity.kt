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
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class SettingsActivity : Activity() {

    private lateinit var settings: SettingsStore
    private val uiScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        Backdrop.apply(this, Backdrop.Scrim.HEAVY)
        settings = SettingsStore(this)

        bind(R.id.sw_dual_screen, settings.dualScreen) {
            settings.dualScreen = it
            LauncherLog.log("Settings: dual screen → $it")
        }
        bindBranch()
        bind(R.id.sw_input_policy, settings.inputPolicy) {
            settings.inputPolicy = it
            LauncherLog.log("Settings: auto touch/controller switching → $it")
        }
        bind(R.id.sw_dual_screen_60, settings.dualScreenFps >= 60) {
            settings.dualScreenFps = if (it) 60 else 30
            LauncherLog.log("Settings: lower screen ${settings.dualScreenFps} FPS")
        }
        // Into dualscreen.json (GamePrep), for trying each off on device.
        bind(R.id.sw_present_thread, settings.presentThread) {
            settings.presentThread = it
            LauncherLog.log("Settings: lower screen present thread → $it")
        }
        bind(R.id.sw_stagger_captures, settings.staggerCaptures) {
            settings.staggerCaptures = it
            LauncherLog.log("Settings: lower screen staggered captures → $it")
        }
        bind(R.id.sw_pause_hidden_pages, settings.pauseHiddenPages) {
            settings.pauseHiddenPages = it
            LauncherLog.log("Settings: lower screen pause hidden pages → $it")
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

        findViewById<Button>(R.id.btn_home_shortcut).setOnClickListener { addShortcut(it as Button) }
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
        bind(R.id.sw_combat_code, settings.combatCodeWarmup) { on ->
            settings.combatCodeWarmup = on
            edit("combat code warmup → $on") { it.put(GameSettingsFile.KEY_COMBAT_CODE_WARMUP, on) }
        }
        bind(R.id.sw_vfx_pool, settings.vfxPool) { on ->
            settings.vfxPool = on
            val scope = if (on) GameSettingsFile.VFX_POOL_ALL else GameSettingsFile.VFX_POOL_STOCK
            edit("vfx pool → $scope") { it.put(GameSettingsFile.KEY_VFX_POOL_SCOPE, scope) }
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
            R.id.sp_fps_limit, GameSettingsFile.FPS_LIMITS, settingsJson.optInt(GameSettingsFile.KEY_FPS_LIMIT, 60),
            { if (it > 0) "$it" else getString(R.string.settings_fps_limit_none) },
        ) { limit -> edit("fps limit → $limit") { it.put(GameSettingsFile.KEY_FPS_LIMIT, limit) } }
        choose(
            R.id.sp_frame_pacing, PACING_NAMES.keys.toList(), settings.framePacing,
            { PACING_NAMES[it]?.let(::getString) ?: it },
        ) { mode ->
            settings.framePacing = mode
            LauncherLog.log("Settings: frame pacing → $mode")
        }
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
        // The launcher's own: GamePrep sets MONO_GC_PARAMS before the engine starts.
        choose(
            R.id.sp_gc, GC_NAMES.keys.toList(), settings.gcNursery,
            { GC_NAMES[it]?.let(::getString) ?: it },
        ) { mode ->
            settings.gcNursery = mode
            LauncherLog.log("Settings: GC nursery → $mode")
        }
        // The launcher's own: GamePrep writes it into dualscreen.json.
        bind(R.id.sw_room_gc, settings.roomGc) { on ->
            settings.roomGc = on
            LauncherLog.log("Settings: room GC → $on")
        }
        bind(R.id.sw_perf_hint, settings.perfHint) { on ->
            settings.perfHint = on
            LauncherLog.log("Settings: performance hint → $on")
        }
        bind(R.id.sw_save_pipelines, settings.savePipelinesOften) { on ->
            settings.savePipelinesOften = on
            LauncherLog.log("Settings: save shader pipelines often → $on")
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

    override fun onDestroy() {
        uiScope.cancel()
        super.onDestroy()
    }

    // The game's icon comes from Steam, so this needs the sign-in and a connection.
    private fun addShortcut(button: Button) {
        val credentials = TokenStore(this).read()
        if (credentials == null) {
            toast(getString(R.string.settings_shortcut_signed_out))
            return
        }
        if (!Network.isOnline(this)) {
            toast(getString(R.string.settings_shortcut_offline))
            return
        }

        button.isEnabled = false
        uiScope.launch {
            try {
                when (HomeShortcut.add(this@SettingsActivity, credentials)) {
                    // The home screen shows its own confirmation.
                    HomeShortcut.Result.REQUESTED -> Unit
                    HomeShortcut.Result.UPDATED -> toast(getString(R.string.settings_shortcut_updated))
                    HomeShortcut.Result.UNSUPPORTED -> toast(getString(R.string.settings_shortcut_unsupported))
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                LauncherLog.warn("Home shortcut failed: $e")
                val reason = if (e.isSteamAuthExpired()) getString(R.string.settings_shortcut_reauth) else e.message ?: e.javaClass.simpleName
                toast(getString(R.string.settings_shortcut_failed, reason))
            } finally {
                button.isEnabled = true
            }
        }
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_LONG).show()

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
            R.id.sp_render_size, R.id.sp_msaa, R.id.sp_vsync, R.id.sp_fps_limit,
        )
        val PACING_NAMES = linkedMapOf(
            EngineOverrides.PACING_STEADY to R.string.settings_pacing_steady,
            EngineOverrides.PACING_ADAPTIVE to R.string.settings_pacing_adaptive,
            EngineOverrides.PACING_OFF to R.string.settings_pacing_off,
        )
        val REFRESH_NAMES = linkedMapOf(
            SettingsStore.REFRESH_SYSTEM to R.string.settings_refresh_system,
            SettingsStore.REFRESH_60HZ to R.string.settings_refresh_60,
            SettingsStore.REFRESH_HIGH to R.string.settings_refresh_high,
        )
        val GC_NAMES = linkedMapOf(
            SettingsStore.GC_DEFAULT to R.string.settings_gc_default,
            SettingsStore.GC_16M to R.string.settings_gc_16m,
            SettingsStore.GC_32M to R.string.settings_gc_32m,
            SettingsStore.GC_64M to R.string.settings_gc_64m,
            SettingsStore.GC_256M to R.string.settings_gc_256m,
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
