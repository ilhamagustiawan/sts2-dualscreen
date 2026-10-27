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
import android.widget.Button
import android.widget.Switch
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

        findViewById<Button>(R.id.btn_reset).setOnClickListener { confirmReset() }
        findViewById<Button>(R.id.btn_settings_logs).setOnClickListener {
            startActivity(Intent(this, LogActivity::class.java))
        }
        findViewById<Button>(R.id.btn_settings_back).setOnClickListener { finish() }
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
        val TAB_NAMES = linkedMapOf(
            "map" to "Map",
            "draw" to "Draw pile",
            "discard" to "Discard pile",
            "exhaust" to "Exhaust pile",
            "deck" to "Deck",
        )
    }
}
