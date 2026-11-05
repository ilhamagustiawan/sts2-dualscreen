package dev.sts2ds.dualscreen;

import android.app.Presentation;
import android.content.Context;
import android.content.pm.ActivityInfo;
import android.os.Bundle;
import android.util.Log;
import android.view.Display;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.widget.FrameLayout;

import com.godot.game.RefreshRate;

import dev.sts2ds.launcher.Backdrop;

/**
 * The window on the bottom screen: one SurfaceView, filled by the game through
 * libsts2ds, and a touch listener that queues touches there for the game.
 */
final class DsPresentation extends Presentation implements SurfaceHolder.Callback {
    private static final String TAG = "STS2DualScreen";
    private static final long COVER_FADE_MS = 250;

    /** Whether the boot backdrop starts over the surface. */
    enum Cover { SHOWN, LIFTED }

    private SurfaceView surface;
    private View cover;
    private final Cover startCover;
    private boolean discardGesture;
    private String refreshMode;

    DsPresentation(Context outerContext, Display display, String refreshMode, Cover startCover) {
        super(outerContext, display);
        this.refreshMode = refreshMode;
        this.startCover = startCover;
    }

    /**
     * Asks this screen for the same rate as the top one (see RefreshRate).
     * Left without a preference, it ran at the system's 120 Hz while the top
     * was held at 60, and the system kept trying to reconcile the two: the
     * top screen dropped to 120 and back, a visible flicker, every touch or
     * two down here.
     */
    void applyRefreshRate(String mode) {
        refreshMode = mode;
        Window window = getWindow();
        Display display = getDisplay();
        if (window == null || display == null) return;
        Display.Mode current = display.getMode();
        Display.Mode[] modes = display.getSupportedModes();
        int[] widths = new int[modes.length];
        int[] heights = new int[modes.length];
        float[] rates = new float[modes.length];
        for (int i = 0; i < modes.length; i++) {
            widths[i] = modes[i].getPhysicalWidth();
            heights[i] = modes[i].getPhysicalHeight();
            rates[i] = modes[i].getRefreshRate();
        }
        int chosen = RefreshRate.choose(widths, heights, rates, current.getPhysicalWidth(), current.getPhysicalHeight(), mode);
        int modeId = chosen >= 0 ? modes[chosen].getModeId() : 0;
        float hz = chosen >= 0 ? modes[chosen].getRefreshRate() : 0f;
        WindowManager.LayoutParams lp = window.getAttributes();
        if (lp.preferredDisplayModeId != modeId || lp.preferredRefreshRate != hz) {
            lp.preferredDisplayModeId = modeId;
            lp.preferredRefreshRate = hz;
            window.setAttributes(lp);
        }
        Surface target = surface == null ? null : surface.getHolder().getSurface();
        if (android.os.Build.VERSION.SDK_INT >= 30 && target != null && target.isValid()) {
            try {
                // 0 withdraws the vote.
                target.setFrameRate(hz, Surface.FRAME_RATE_COMPATIBILITY_DEFAULT);
            } catch (RuntimeException e) {
                Log.w(TAG, "bottom setFrameRate failed", e);
            }
        }
        if (dev.sts2ds.BuildConfig.DEBUG) Log.i(TAG, "Bottom screen refresh " + mode + ": mode " + modeId + " (" + hz + " Hz)");
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Window window = getWindow();
        if (window != null) {
            window.setBackgroundDrawableResource(android.R.color.black);
            // A Presentation is a real window and takes key focus if allowed.
            // Gamepad and key events follow focus, so without this the
            // controller would stop reaching the game on the top screen.
            // Touch is unaffected by this flag.
            window.addFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            // A Presentation does not inherit the activity's orientation. The
            // Thor's bottom panel is natively portrait; keep this window
            // landscape so the surface size never transposes mid-game.
            WindowManager.LayoutParams lp = window.getAttributes();
            lp.screenOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE;
            window.setAttributes(lp);
        }
        surface = new SurfaceView(getContext());
        surface.getHolder().addCallback(this);
        surface.setOnTouchListener((view, event) -> onTouch(event, view.getWidth(), view.getHeight()));
        surface.setOnGenericMotionListener((view, event) ->
            (event.getSource() & InputDevice.SOURCE_CLASS_POINTER) != 0);
        FrameLayout content = new FrameLayout(getContext());
        content.addView(surface);
        if (startCover == Cover.SHOWN) {
            // Not clickable, so touches fall through to the surface.
            cover = new View(getContext());
            cover.setBackground(Backdrop.drawable(getContext(), Backdrop.Scrim.NONE));
            content.addView(cover);
        }
        setContentView(content);
        applyRefreshRate(refreshMode);
    }

    /** Fades out the boot backdrop; the game has something of its own to show here now. */
    void liftCover() {
        View old = cover;
        if (old == null) {
            return;
        }
        cover = null;
        old.animate().alpha(0f).setDuration(COVER_FADE_MS).withEndAction(() -> {
            if (old.getParent() instanceof ViewGroup) {
                ((ViewGroup) old.getParent()).removeView(old);
            }
        });
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        // Backstop to FLAG_NOT_FOCUSABLE: never consume a key here.
        return false;
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {}

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        DsNative.nativeSetSurface(holder.getSurface(), width, height);
        applyRefreshRate(refreshMode);
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        DsNative.nativeSetSurface(null, 0, 0);
    }

    private boolean onTouch(MotionEvent event, int width, int height) {
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) discardGesture = false;
        if (discardGesture) return true;
        if (width <= 0 || height <= 0) {
            discardGesture = true;
            return true;
        }

        int first = 0;
        int count = event.getPointerCount();
        int phase;
        switch (action) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN:
                phase = DsNative.PHASE_DOWN;
                first = event.getActionIndex();
                count = first + 1;
                break;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP:
                phase = DsNative.PHASE_UP;
                first = event.getActionIndex();
                count = first + 1;
                break;
            case MotionEvent.ACTION_MOVE:
                phase = DsNative.PHASE_MOVE;
                break;
            case MotionEvent.ACTION_CANCEL:
                phase = DsNative.PHASE_CANCEL;
                break;
            default:
                return true;
        }
        for (int i = first; i < count; i++) {
            if (!DsNative.nativeTouch(event.getPointerId(i), phase,
                    event.getX(i) / width, event.getY(i) / height)) {
                Log.w(TAG, "Bottom-screen touch queue overflowed; dropped the gesture");
                discardGesture = true;
                break;
            }
        }
        // Never pass this on: the game reads these touches itself.
        return true;
    }
}
