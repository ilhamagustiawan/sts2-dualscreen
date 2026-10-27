package com.godot.game;

import android.app.Activity;
import android.os.Build;
import android.util.Log;
import android.view.Display;
import android.view.Surface;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;

/**
 * Asks the top screen for a refresh rate while the game runs: 60 Hz, the
 * highest the panel offers, or nothing ("system"). The mode choice is
 * Sts2MobileLauncher's HighRefreshRateController (MIT), trimmed to one window
 * request plus a frame-rate vote on the game's surface; see NOTICE.md.
 *
 * Only the activity's own window is touched, so the bottom screen's
 * Presentation keeps whatever its display runs at.
 */
public final class RefreshRate {
    private static final String TAG = "GameActivity";
    public static final String SYSTEM = "system";
    public static final String HZ60 = "60hz";
    public static final String HIGH = "high";
    private static final float MIN_HIGH_HZ = 61f;
    private static final float TOLERANCE_HZ = 0.5f;

    private RefreshRate() {}

    /**
     * Index into the given modes to request, or -1 for none. Only modes with
     * the current physical size are considered: a resolution switch is never
     * worth a refresh rate.
     */
    public static int choose(int[] widths, int[] heights, float[] rates,
                             int currentWidth, int currentHeight, String mode) {
        if (!HZ60.equals(mode) && !HIGH.equals(mode)) return -1;
        int best = -1;
        for (int i = 0; i < rates.length; i++) {
            if (widths[i] != currentWidth || heights[i] != currentHeight) continue;
            if (HZ60.equals(mode)) {
                if (Math.abs(rates[i] - 60f) > TOLERANCE_HZ) continue;
                if (best < 0 || Math.abs(rates[i] - 60f) < Math.abs(rates[best] - 60f)) best = i;
            } else {
                if (rates[i] < MIN_HIGH_HZ) continue;
                if (best < 0 || rates[i] > rates[best]) best = i;
            }
        }
        return best;
    }

    /** Applies [mode] to the activity's window and to the game's surface. */
    public static void apply(Activity activity, String mode) {
        Window window = activity.getWindow();
        Display display = Build.VERSION.SDK_INT >= 30 ? activity.getDisplay() : activity.getWindowManager().getDefaultDisplay();
        if (window == null || display == null) return;

        int modeId = 0;
        float hz = 0f;
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
        int chosen = choose(widths, heights, rates, current.getPhysicalWidth(), current.getPhysicalHeight(), mode);
        if (chosen >= 0) {
            modeId = modes[chosen].getModeId();
            hz = modes[chosen].getRefreshRate();
        } else if (!SYSTEM.equals(mode)) {
            Log.i(TAG, "Refresh rate " + mode + ": no matching mode; leaving it to the system");
        }

        WindowManager.LayoutParams lp = window.getAttributes();
        if (lp.preferredDisplayModeId != modeId || lp.preferredRefreshRate != hz) {
            lp.preferredDisplayModeId = modeId;
            lp.preferredRefreshRate = hz;
            window.setAttributes(lp);
        }
        if (Build.VERSION.SDK_INT >= 30) {
            SurfaceView view = findSurfaceView(window.getDecorView());
            Surface surface = view == null ? null : view.getHolder().getSurface();
            if (surface != null && surface.isValid()) {
                try {
                    // 0 withdraws the vote.
                    surface.setFrameRate(hz, Surface.FRAME_RATE_COMPATIBILITY_DEFAULT);
                } catch (RuntimeException e) {
                    Log.w(TAG, "setFrameRate failed", e);
                }
            }
        }
        Log.i(TAG, "Refresh rate " + mode + ": mode " + modeId + " (" + hz + " Hz), display now " + display.getRefreshRate() + " Hz");
    }

    private static SurfaceView findSurfaceView(View view) {
        if (view instanceof SurfaceView) return (SurfaceView) view;
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++) {
                SurfaceView found = findSurfaceView(group.getChildAt(i));
                if (found != null) return found;
            }
        }
        return null;
    }
}
