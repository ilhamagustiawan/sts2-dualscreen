package dev.sts2ds.launcher

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.util.AttributeSet
import android.view.View
import android.view.animation.AccelerateDecelerateInterpolator
import androidx.core.content.ContextCompat
import dev.sts2ds.R

/**
 * The boot cover's loading bar: a dark rounded track edged in gold, with a
 * glowing gold segment sweeping across it. Indeterminate; the game reports
 * no progress while it boots.
 *
 *   ╭──────────────────────────────────────╮
 *   │        ░▒▓████▓▒░                    │  → sweeps, then repeats
 *   ╰──────────────────────────────────────╯
 */
class LoadingBar @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : View(context, attrs) {

    private companion object {
        const val SWEEP_MS = 1400L
        const val SEGMENT_FRACTION = 0.35f
        const val BORDER_DP = 1.5f
        const val INSET_DP = 2.5f
        const val TRACK_COLOR = 0x8C0E0B09.toInt()
        const val BORDER_ALPHA = 150
    }

    private val gold = ContextCompat.getColor(context, R.color.title_gold)
    private val density = resources.displayMetrics.density

    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = TRACK_COLOR }
    private val border = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = BORDER_DP * density
        color = gold
        alpha = BORDER_ALPHA
    }
    private val segment = Paint(Paint.ANTI_ALIAS_FLAG)

    private val outer = RectF()
    private val inner = RectF()
    private val clip = Path()
    private var phase = 0f

    private val sweep = ValueAnimator.ofFloat(0f, 1f).apply {
        duration = SWEEP_MS
        repeatCount = ValueAnimator.INFINITE
        interpolator = AccelerateDecelerateInterpolator()
        addUpdateListener {
            phase = it.animatedValue as Float
            invalidate()
        }
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        // Track fills the view; the segment runs inside it, clear of the border.
        val half = border.strokeWidth / 2f
        outer.set(half, half, w - half, h - half)
        val inset = INSET_DP * density
        inner.set(inset, inset, w - inset, h - inset)
        clip.reset()
        clip.addRoundRect(inner, inner.height() / 2f, inner.height() / 2f, Path.Direction.CW)

        // Fades in, glows white-gold at the centre, fades out.
        val segW = inner.width() * SEGMENT_FRACTION
        val clear = Color.argb(0, Color.red(gold), Color.green(gold), Color.blue(gold))
        segment.shader = LinearGradient(
            0f, 0f, segW, 0f,
            intArrayOf(clear, gold, Color.WHITE, gold, clear),
            floatArrayOf(0f, 0.3f, 0.5f, 0.7f, 1f),
            Shader.TileMode.CLAMP,
        )
    }

    override fun onDraw(canvas: Canvas) {
        val radius = outer.height() / 2f
        canvas.drawRoundRect(outer, radius, radius, track)

        // Starts fully off the left edge, ends fully off the right.
        val segW = inner.width() * SEGMENT_FRACTION
        val x = inner.left - segW + phase * (inner.width() + segW)
        canvas.save()
        canvas.clipPath(clip)
        canvas.translate(x, 0f)
        canvas.drawRect(0f, inner.top, segW, inner.bottom, segment)
        canvas.restore()

        canvas.drawRoundRect(outer, radius, radius, border)
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        sweep.start()
    }

    override fun onDetachedFromWindow() {
        sweep.cancel()
        super.onDetachedFromWindow()
    }
}
