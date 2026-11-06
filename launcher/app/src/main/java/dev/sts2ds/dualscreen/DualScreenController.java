package dev.sts2ds.dualscreen;

import android.app.Activity;
import android.content.Context;
import android.hardware.display.DisplayManager;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import dev.sts2ds.AppLog;
import android.view.Display;

/**
 * Finds the second display, keeps a {@link DsPresentation} on it, and follows
 * the game activity's lifecycle.
 *
 * The display is found through DisplayManager's presentation category rather
 * than by device model, and re-found when displays come and go (the Thor can
 * switch its bottom screen off). With no second display nothing happens and
 * the game plays exactly as it would on a single screen.
 */
public final class DualScreenController implements DisplayManager.DisplayListener {
    private static final String TAG = "STS2DualScreen";
    private static boolean libraryLoaded;

    private final Activity activity;
    private final DisplayManager displays;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private DsPresentation presentation;
    private boolean started;
    private boolean destroyed;
    private boolean coverLifted;
    private float lastTopHz;
    private int topRateChanges;
    private String refreshMode;

    /** [refreshMode] is the top screen's (see RefreshRate); the bottom one follows it. */
    public DualScreenController(Activity activity, String refreshMode) {
        this.activity = activity;
        this.displays = (DisplayManager) activity.getSystemService(Context.DISPLAY_SERVICE);
        this.refreshMode = refreshMode;
    }

    /** Lifts the boot backdrop on the bottom screen, now and for any later presentation. Main thread. */
    public void liftCover() {
        coverLifted = true;
        if (presentation != null) {
            presentation.liftCover();
        }
    }

    public void setRefreshMode(String mode) {
        refreshMode = mode;
        if (presentation != null) presentation.applyRefreshRate(mode);
    }

    /** Loads libsts2ds. Must happen before the game's C# side looks for it. */
    public static synchronized boolean loadLibrary() {
        if (!libraryLoaded) {
            try {
                System.loadLibrary("sts2ds");
                libraryLoaded = true;
            } catch (UnsatisfiedLinkError error) {
                Log.e(TAG, "libsts2ds missing; dual screen disabled", error);
            }
        }
        return libraryLoaded;
    }

    public void onCreate() {
        if (!loadLibrary()) return;
        displays.registerDisplayListener(this, handler);
    }

    public void onStart() {
        started = true;
        refresh();
    }

    public void onStop() {
        // onStop, not focus loss: touching the other display moves focus while
        // the game is still on screen. onStop means it really was hidden.
        started = false;
        refresh();
    }

    public void onDestroy() {
        destroyed = true;
        if (libraryLoaded) displays.unregisterDisplayListener(this);
        dismiss();
    }

    @Override public void onDisplayAdded(int displayId) { refresh(); }
    @Override public void onDisplayRemoved(int displayId) { refresh(); }
    @Override public void onDisplayChanged(int displayId) {
        logTopRefreshRate(displayId);
        refresh();
    }

    // A panel switching refresh rate blanks for a moment; log each switch of
    // the top screen (the first 20) to tell that apart from other flicker.
    private void logTopRefreshRate(int displayId) {
        Display main = activity.getWindowManager().getDefaultDisplay();
        if (displayId != main.getDisplayId()) return;
        float hz = main.getRefreshRate();
        if (Math.abs(hz - lastTopHz) < 0.5f) return;
        if (lastTopHz > 0 && topRateChanges++ < 20) {
            AppLog.i(TAG, "Top screen refresh " + Math.round(lastTopHz) + " -> " + Math.round(hz) + " Hz");
        }
        lastTopHz = hz;
    }

    private Display findSecondDisplay() {
        int main = activity.getWindowManager().getDefaultDisplay().getDisplayId();
        for (Display display : displays.getDisplays(DisplayManager.DISPLAY_CATEGORY_PRESENTATION)) {
            if (display.getDisplayId() != main && display.getState() != Display.STATE_OFF) {
                return display;
            }
        }
        return null;
    }

    private void refresh() {
        if (!libraryLoaded || destroyed) return;
        Display target = started ? findSecondDisplay() : null;
        if (presentation != null) {
            Display current = presentation.getDisplay();
            if (target != null && current.getDisplayId() == target.getDisplayId()) {
                DsNative.nativeSetVisible(true);
                return;
            }
            dismiss();
        }
        if (target == null) return;
        try {
            presentation = new DsPresentation(activity, target, refreshMode,
                coverLifted ? DsPresentation.Cover.LIFTED : DsPresentation.Cover.SHOWN);
            presentation.setOnDismissListener(dialog -> {
                if (presentation == dialog) presentation = null;
                DsNative.nativeSetVisible(false);
            });
            presentation.show();
            DsNative.nativeSetVisible(true);
            AppLog.i(TAG, "Bottom screen on display " + target.getDisplayId() + " (" + target.getName() + ")");
        } catch (RuntimeException e) {
            // WindowManager.InvalidDisplayException and friends: the display
            // went away between finding it and showing on it.
            Log.w(TAG, "Could not show on display " + target.getDisplayId(), e);
            presentation = null;
        }
    }

    private void dismiss() {
        if (libraryLoaded) DsNative.nativeSetVisible(false);
        if (presentation != null) {
            DsPresentation old = presentation;
            presentation = null;
            try {
                old.dismiss();
            } catch (RuntimeException ignored) {
            }
        }
    }
}
