package dev.sts2ds.launcher

import android.app.Activity
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.drawable.Drawable
import android.view.ViewGroup
import dev.sts2ds.R
import java.io.File

/**
 * The backdrop behind every launcher screen and the game's boot covers: the
 * bundled bg_start art. The game's logo for the boot covers is shot by the
 * mod (DsBackdrop.cs) on the first launch of each build.
 */
object Backdrop {
    // Written by DsBackdrop.cs; keep the name in step.
    private const val LOGO_NAME = "backdrop_logo.png"

    // Where the spire stands across the art, 0..1; crops keep it in view.
    private const val FOCUS_X = 0.47f

    /** How much to darken the art, so what sits on it stays legible. */
    enum class Scrim(val alpha: Int) {
        NONE(0),
        LIGHT(70),
        HEAVY(170),
    }

    private var cached: Bitmap? = null

    /** The backdrop for [context], darkened by [scrim]. */
    @JvmStatic
    fun drawable(context: Context, scrim: Scrim): Drawable {
        return Art(load(context), scrim.alpha)
    }

    /** The game's logo on transparency, or null until the mod has shot it. */
    @JvmStatic
    fun logo(context: Context): Bitmap? {
        val file = File(context.filesDir, LOGO_NAME)
        if (!file.exists()) {
            return null
        }
        return runCatching { BitmapFactory.decodeFile(file.path) }.getOrNull()
    }

    /** Puts the backdrop behind [activity]'s content, replacing its root's background. */
    @JvmStatic
    fun apply(activity: Activity, scrim: Scrim) {
        val content = activity.findViewById<ViewGroup>(android.R.id.content)
        val root = content.getChildAt(0) ?: content
        root.background = drawable(activity, scrim)
    }

    // Decoded once per process.
    @Synchronized
    private fun load(context: Context): Bitmap =
        cached ?: BitmapFactory.decodeResource(context.resources, R.drawable.bg_start).also { cached = it }

    // Covers its bounds like centerCrop, but crops around the spire.
    private class Art(private val bitmap: Bitmap, scrimAlpha: Int) : Drawable() {
        private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
        private val scrim = Paint().apply { color = android.graphics.Color.argb(scrimAlpha, 0, 0, 0) }
        private val src = Rect()

        override fun onBoundsChange(bounds: Rect) {
            val w = bitmap.width
            val h = bitmap.height
            if (bounds.isEmpty) {
                return
            }

            // Source rect with the bounds' aspect, as large as the bitmap allows.
            val aspect = bounds.width().toFloat() / bounds.height()
            var cropW = w
            var cropH = (w / aspect).toInt()
            if (cropH > h) {
                cropH = h
                cropW = (h * aspect).toInt()
            }

            val left = (FOCUS_X * w - cropW / 2f).toInt().coerceIn(0, w - cropW)
            val top = (h - cropH) / 2
            src.set(left, top, left + cropW, top + cropH)
        }

        override fun draw(canvas: Canvas) {
            canvas.drawBitmap(bitmap, src, bounds, paint)
            if (scrim.alpha > 0) {
                canvas.drawRect(bounds, scrim)
            }
        }

        override fun setAlpha(alpha: Int) {
            paint.alpha = alpha
        }

        override fun setColorFilter(colorFilter: ColorFilter?) {
            paint.colorFilter = colorFilter
        }

        @Deprecated("Deprecated in Java")
        override fun getOpacity() = PixelFormat.OPAQUE
    }
}
