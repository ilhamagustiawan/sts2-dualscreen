using System;
using System.Threading.Tasks;
using HarmonyLib;

namespace STS2DualScreen;

// The compat pack's preload after the main menu, under the boot cover.
//
// Once the menu is ready the compat pack shows its own loading screen
// ("Optimizing startup..."), loads the Common and MainMenu sets again, one at
// a time on the main thread, and then warms the vfx scenes. Here its screen is
// left out: the launcher's boot cover ("Loading...") stays up instead until
// the preload ends, its line naming each step (DsBootCover.HoldTop).
//
//   boot cover: Loading assets 312/790 -> Compiling combat code
//            -> Rendering combat effects -> ... -> lifted, menu usable
//
// With the game's own preload on, the game is already loading those sets in
// its 'Common' session, so the second pass only competes with it (some 10 s
// on the Thor). Here that pass waits for the game's session instead.
internal static class DsDeferredPreload
{
    private const string PatchesType = "STS2Mobile.Patches.LifecycleAndPerformancePatches";
    private const string ScreenType = "STS2Mobile.Patches.AndroidStartupLoadingScreen";
    private static readonly TimeSpan CommonWaitLimit = TimeSpan.FromSeconds(60);

    private const string StepGameplayAssets = "Loading gameplay assets";
    private const string StepMenus = "Preparing menus";

    private static Func<bool> _originalCommonPreload;
    private static AccessTools.FieldRef<bool> _preloadStarted;
    private static object _noScreen;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var patches = AccessTools.TypeByName(PatchesType);
            var screen = AccessTools.TypeByName(ScreenType);
            if (patches == null || screen == null)
            {
                DsLog.Warn("deferred preload: compat preload not found; left as is");
                return;
            }
            var showScreen = AccessTools.Method(patches, "ShowDeferredPreloadScreenAsync", new[] { typeof(Godot.Node) });
            var serialized = AccessTools.Method(patches, "RunSerializedAndroidPreloadAsync", new[] { screen });
            _originalCommonPreload = Getter<bool>(patches, "ShouldUseOriginalCommonMainMenuPreload");

            if (serialized != null && _originalCommonPreload != null)
            {
                harmony.Patch(serialized, prefix: new HarmonyMethod(typeof(DsDeferredPreload), nameof(SerializedPrefix)));
            }

            // The cover replaces the compat screen only if it can be held
            // for the whole preload and lifted at its end.
            var start = AccessTools.Method(patches, "StartSafeDeferredPreload");
            var run = AccessTools.Method(patches, "RunSafeDeferredPreloadAsync");
            var started = AccessTools.Field(patches, "_safePreloadStarted");
            if (showScreen == null || start == null || run == null || started == null)
            {
                DsLog.Warn("deferred preload: compat preload flow not found; its loading screen stays");
                return;
            }
            _preloadStarted = AccessTools.StaticFieldRefAccess<bool>(started);
            _noScreen = AccessTools.Method(typeof(Task), nameof(Task.FromResult))
                .MakeGenericMethod(screen)
                .Invoke(null, new object[] { null });
            harmony.Patch(showScreen, prefix: new HarmonyMethod(typeof(DsDeferredPreload), nameof(ShowScreenPrefix)));
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(DsDeferredPreload), nameof(StartPostfix)));
            harmony.Patch(run, postfix: new HarmonyMethod(typeof(DsDeferredPreload), nameof(RunPostfix)));

            // Steps the compat pack would only have named on its own screen.
            PatchStep(harmony, patches, "RunGameplayWarmCachePreloadAsync", nameof(GameplayAssetsPrefix));
            PatchStep(harmony, patches, "WarmMainMenuHotspotsAsync", nameof(MenusPrefix));
        }
        catch (Exception e)
        {
            DsLog.Warn($"deferred preload: patch failed: {e.Message}");
        }
    }

    private static Func<T> Getter<T>(Type type, string name)
    {
        var method = AccessTools.Method(type, name, Type.EmptyTypes);
        return method == null || method.ReturnType != typeof(T) ? null : method.CreateDelegate<Func<T>>();
    }

    private static void PatchStep(Harmony harmony, Type patches, string method, string prefix)
    {
        var target = AccessTools.Method(patches, method);
        if (target == null)
        {
            DsLog.Warn($"deferred preload: {method} not found; its step goes unnamed");
            return;
        }
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(DsDeferredPreload), prefix));
    }

    // The boot cover covers the preload instead of the compat screen.
    private static bool ShowScreenPrefix(ref object __result)
    {
        __result = _noScreen;
        return false;
    }

    // The preload was scheduled from the main menu's _Ready: keep the cover,
    // which the menu would otherwise lift a couple of frames later.
    private static void StartPostfix()
    {
        if (_preloadStarted())
        {
            DsBootCover.HoldTop();
        }
    }

    // The preload's end, success or not: lift the cover on the game thread.
    private static void RunPostfix(Task __result)
    {
        if (__result == null)
        {
            DsBootCover.ReleaseTop();
            return;
        }
        __result.ContinueWith(_ => Godot.Callable.From(DsBootCover.ReleaseTop).CallDeferred());
    }

    private static void GameplayAssetsPrefix() => DsLoadingDetail.SetStep(StepGameplayAssets, DsLoadingDetail.GameplayAt);

    private static void MenusPrefix() => DsLoadingDetail.SetStep(StepMenus, DsLoadingDetail.MenusAt);

    private static bool SerializedPrefix(ref Task __result)
    {
        try
        {
            if (!_originalCommonPreload())
                return true;
            DsLog.Debug("deferred preload: second Common pass skipped; waiting for the game's own");
            __result = WaitForCommon();
            return false;
        }
        catch (Exception e)
        {
            DsLog.Warn($"deferred preload: running the second Common pass: {e.Message}");
            return true;
        }
    }

    private static async Task WaitForCommon()
    {
        var started = DateTime.UtcNow;
        var done = await Task.WhenAny(DsStartup.CommonPreloadTask, Task.Delay(CommonWaitLimit));
        if (done != DsStartup.CommonPreloadTask)
            DsLog.Warn($"deferred preload: Common still loading after {CommonWaitLimit.TotalSeconds:0} s; warming up anyway");
        else
            DsLog.Info($"deferred preload: waited {(DateTime.UtcNow - started).TotalMilliseconds:0} ms for the game's Common preload");
    }
}
