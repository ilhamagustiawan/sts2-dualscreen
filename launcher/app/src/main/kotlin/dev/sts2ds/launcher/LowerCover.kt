package dev.sts2ds.launcher

import android.app.Activity
import android.app.Presentation
import android.content.Context
import android.content.pm.ActivityInfo
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.view.Display
import android.view.WindowManager
import dev.sts2ds.R

/**
 * The key art on the lower screen while the launcher is up, so that screen
 * looks the same from launch through the game's boot cover (DsPresentation).
 * The display is found as DualScreenController finds it; with no second
 * display nothing happens.
 */
class LowerCover(private val activity: Activity) : DisplayManager.DisplayListener {
    private val displays = activity.getSystemService(DisplayManager::class.java)
    private var shown: Presentation? = null
    private var started = false

    fun onStart() {
        started = true
        displays.registerDisplayListener(this, null)
        refresh()
    }

    fun onStop() {
        started = false
        displays.unregisterDisplayListener(this)
        refresh()
    }

    override fun onDisplayAdded(displayId: Int) = refresh()

    override fun onDisplayRemoved(displayId: Int) = refresh()

    // Also fires when the Thor switches its lower screen off.
    override fun onDisplayChanged(displayId: Int) = refresh()

    private fun refresh() {
        val target = if (started) secondDisplay() else null
        val current = shown
        if (current != null && current.display.displayId == target?.displayId) {
            return
        }

        current?.dismiss()
        shown = null
        if (target == null) {
            return
        }
        shown = runCatching { Cover(activity, target).apply { show() } }
            .onFailure { LauncherLog.warn("Lower screen cover failed: ${it.message}") }
            .getOrNull()
    }

    private fun secondDisplay(): Display? {
        @Suppress("DEPRECATION")
        val main = activity.windowManager.defaultDisplay.displayId
        return displays.getDisplays(DisplayManager.DISPLAY_CATEGORY_PRESENTATION)
            .firstOrNull { it.displayId != main && it.state != Display.STATE_OFF }
    }

    private class Cover(context: Context, display: Display) : Presentation(context, display) {
        override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)

            // Keys stay with the launcher on the top screen; landscape as the game's window is.
            window?.let { w ->
                w.addFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE)
                w.attributes = w.attributes.apply { screenOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
            }

            setContentView(R.layout.lower_cover)
            window?.decorView?.background = Backdrop.drawable(context, Backdrop.Scrim.NONE)
        }
    }
}
