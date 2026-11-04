package com.godot.game;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Bitmap;
import android.os.Bundle;
import android.util.Log;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;

import androidx.core.splashscreen.SplashScreen;

import org.fmod.FMOD;
import org.godotengine.godot.Godot;
import org.godotengine.godot.GodotActivity;
import org.godotengine.godot.plugin.GodotPlugin;

import java.io.File;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Set;

import org.json.JSONObject;

import dev.sts2ds.R;
import dev.sts2ds.dualscreen.DualScreenController;
import dev.sts2ds.launcher.Backdrop;
import dev.sts2ds.launcher.GamePrep;
import dev.sts2ds.launcher.GameSettingsFile;
import dev.sts2ds.launcher.LauncherActivity;
import dev.sts2ds.launcher.SettingsActivity;
import dev.sts2ds.launcher.SettingsStore;

/**
 * The activity the game runs in, on the top screen.
 *
 * Named com.godot.game.GodotApp on purpose: Sts2MobileLauncher's compatibility
 * pack calls a few static methods on that class through JavaClassWrapper
 * (orientation, refresh rate, opening settings). Every one of those calls is
 * optional on the C# side, but keeping the name keeps them working.
 *
 * The launcher has already run {@link GamePrep} by the time this starts; it
 * runs again here only as a cheap check, so a launch from a stale shortcut
 * cannot boot a half-staged game.
 */
public class GodotApp extends GodotActivity {
    private static final String TAG = "GameActivity";
    private static final long BOOT_COVER_TIMEOUT_MS = 60_000;
    private static final long BOOT_COVER_FADE_MS = 250;
    private static volatile GodotApp current;

    static {
        loadOptional("fmod");
        loadOptional("fmodstudio");
        // Lets Harmony/MonoMod patch on ROMs that refuse W^X page flips.
        loadOptional("monomod_android_libc_shim");
        loadOptional("System.Security.Cryptography.Native.Android");
        DualScreenController.loadLibrary();
    }

    private DualScreenController dualScreen;
    private View bootCover;
    private GamePrep.Prepared prepared;
    private String refreshRate = RefreshRate.SYSTEM;
    private GodotInputSourceHost inputSourceHost;
    private final Runnable applyRefreshRate = () -> RefreshRate.apply(this, refreshRate);

    private static void loadOptional(String name) {
        try {
            System.loadLibrary(name);
        } catch (UnsatisfiedLinkError error) {
            Log.w(TAG, "Could not preload lib" + name + ".so", error);
        }
    }

    @Override
    public void onCreate(Bundle savedInstanceState) {
        SplashScreen.installSplashScreen(this);
        try {
            prepared = GamePrep.prepare(this, false);
        } catch (Exception e) {
            Log.e(TAG, "Game is not ready; back to the launcher", e);
            super.onCreate(savedInstanceState);
            startActivity(new Intent(this, LauncherActivity.class)
                .putExtra(LauncherActivity.EXTRA_ERROR, String.valueOf(e.getMessage())));
            finish();
            return;
        }
        try {
            FMOD.init(this);
        } catch (Throwable t) {
            Log.w(TAG, "FMOD init failed", t);
        }
        super.onCreate(savedInstanceState);
        current = this;
        refreshRate = prepared.getRefreshRate();
        showBootCover();
        if (prepared.getDualScreen()) {
            dualScreen = new DualScreenController(this, refreshRate);
            dualScreen.onCreate();
        }
    }

    // Godot draws nothing for its first several seconds; this covers that with
    // the launcher's backdrop until the mod says the game has drawn (see
    // liftBootCoverFromGame), or the timeout, whichever comes first.
    private void showBootCover() {
        bootCover = getLayoutInflater().inflate(R.layout.boot_cover, null);
        bootCover.setBackground(Backdrop.drawable(this, Backdrop.Scrim.LIGHT));

        // The game's painted logo replaces the plain title once it exists.
        Bitmap logo = Backdrop.logo(this);
        if (logo != null) {
            ImageView image = bootCover.findViewById(R.id.boot_logo);
            image.setImageBitmap(logo);
            image.setVisibility(View.VISIBLE);
            bootCover.findViewById(R.id.boot_title).setVisibility(View.GONE);
        }
        addContentView(bootCover, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        bootCover.postDelayed(() -> {
            liftBootCover("timeout");
            if (dualScreen != null) {
                dualScreen.liftCover();
            }
        }, BOOT_COVER_TIMEOUT_MS);
    }

    private void liftBootCover(String why) {
        View cover = bootCover;
        if (cover == null) {
            return;
        }
        bootCover = null;
        Log.i(TAG, "Boot cover lifted (" + why + ")");
        cover.animate().alpha(0f).setDuration(BOOT_COVER_FADE_MS).withEndAction(() -> {
            if (cover.getParent() instanceof ViewGroup) {
                ((ViewGroup) cover.getParent()).removeView(cover);
            }
        });
    }

    @Override
    public List<String> getCommandLine() {
        List<String> args = new ArrayList<>(super.getCommandLine());
        if (prepared != null) {
            args.add("--main-pack");
            args.add(prepared.getPck().getAbsolutePath());
            args.add("--log-file");
            args.add(new File(prepared.getLogsDir(), "godot.log").getAbsolutePath());
            // As Sts2MobileLauncher's renderer setting: the game's pack asks
            // for Vulkan (Forward Mobile); this switches to OpenGL ES 3.
            if (SettingsStore.RENDERER_OPENGL.equals(prepared.getRenderer())) {
                args.add("--rendering-method");
                args.add("gl_compatibility");
            }
        }
        // The desktop build would try to start Steamworks; on Android the
        // Steam side is the launcher's job.
        args.add("--force-steam");
        args.add("off");
        Log.i(TAG, "Godot command line: " + args);
        return args;
    }

    @Override
    public void onStart() {
        super.onStart();
        if (dualScreen != null) dualScreen.onStart();
    }

    @Override
    public void onResume() {
        super.onResume();
        requestRefreshRate();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (inputSourceHost != null) inputSourceHost.onWindowFocusChanged(hasFocus);
        if (hasFocus) requestRefreshRate();
    }

    // The compat pack's touch/controller policy learns from this plugin which
    // device each input came from (Sts2MobileLauncher's GodotApp). Without it
    // the policy stays off. Asked once, while super.onCreate starts Godot.
    @Override
    public Set<GodotPlugin> getHostPlugins(Godot godot) {
        if (prepared == null || !prepared.getInputPolicy()) {
            return Collections.emptySet();
        }
        if (inputSourceHost == null) {
            inputSourceHost = new GodotInputSourceHost(godot);
        }
        return Collections.singleton(inputSourceHost);
    }

    // Once now, and again once the game's surface exists to take the vote.
    private void requestRefreshRate() {
        if (prepared == null) return;
        View decor = getWindow().getDecorView();
        decor.removeCallbacks(applyRefreshRate);
        applyRefreshRate.run();
        decor.postDelayed(applyRefreshRate, 500);
    }

    @Override
    public void onPause() {
        GamePrep.clearAnimWarmupMarker(this);
        super.onPause();
    }

    @Override
    public void onStop() {
        if (dualScreen != null) dualScreen.onStop();
        super.onStop();
    }

    @Override
    public void onDestroy() {
        if (dualScreen != null) dualScreen.onDestroy();
        try {
            FMOD.close();
        } catch (Throwable ignored) {
        }
        if (current == this) current = null;
        super.onDestroy();
    }

    // ── Called from the dual-screen mod (JavaClassWrapper) ─────────────────

    /** The main menu has drawn on the top screen. */
    public static boolean liftBootCoverFromGame() {
        GodotApp activity = current;
        if (activity == null) {
            return false;
        }
        activity.runOnUiThread(() -> activity.liftBootCover("main menu"));
        return true;
    }

    /** The bottom screen has its own picture now (menu art or a run). */
    public static boolean liftBottomCoverFromGame() {
        GodotApp activity = current;
        if (activity == null) {
            return false;
        }
        activity.runOnUiThread(() -> {
            if (activity.dualScreen != null) {
                activity.dualScreen.liftCover();
            }
        });
        return true;
    }

    // ── Called from the compatibility pack (JavaClassWrapper) ──────────────

    /** The activity is locked landscape; nothing to apply. */
    public static void applySelectedScreenOrientationFromGame() {}

    /**
     * The compat pack's in-game "high refresh rate" toggle has just written
     * settings.save. Applies for this session; GamePrep carries it into the
     * launcher's setting on the next launch.
     */
    public static void applySelectedDisplayRefreshRateFromGame() {
        GodotApp activity = current;
        if (activity == null) return;
        JSONObject json = new GameSettingsFile(activity).read();
        Boolean high = json != null && json.has(GameSettingsFile.KEY_HIGH_REFRESH)
            ? json.optBoolean(GameSettingsFile.KEY_HIGH_REFRESH) : null;
        String mode = GameSettingsFile.Companion.refreshAfterGame(activity.refreshRate, high);
        activity.runOnUiThread(() -> {
            activity.refreshRate = mode;
            activity.requestRefreshRate();
            if (activity.dualScreen != null) activity.dualScreen.setRefreshMode(mode);
        });
    }

    // ── Called from the compatibility pack (JavaClassWrapper) ──────────────

    public static boolean launchGameSettingsFromGame() {
        Activity activity = current;
        if (activity == null) return false;
        activity.runOnUiThread(() -> activity.startActivity(new Intent(activity, SettingsActivity.class)));
        return true;
    }

    /**
     * The compat pack's replacement for the game's Quit. Despite the name, it
     * goes back to the launcher, which then uploads the saves (auto-push).
     */
    public static boolean restartToSettingsFromGame() {
        Activity activity = current;
        if (activity == null) return false;
        activity.runOnUiThread(() -> {
            activity.startActivity(new Intent(activity, LauncherActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            activity.finish();
            // Godot cannot be restarted inside one process.
            android.os.Process.killProcess(android.os.Process.myPid());
        });
        return true;
    }
}
