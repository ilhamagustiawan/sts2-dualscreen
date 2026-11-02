using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace STS2DualScreen;

// The compat pack's preload after the main menu ("Optimizing startup..."),
// kept from covering a menu that is already usable.
//
// Once the menu is ready the compat pack shows a full-screen loading screen,
// loads the Common and MainMenu sets again, one at a time on the main thread,
// and then warms the vfx scenes. With the game's own preload on, the game is
// already loading those sets in its 'Common' session, so the second pass only
// competes with it (some 10 s on the Thor). Here that pass waits for the
// game's session instead, and the screen is left out when what follows is
// cheap: the warmup cache is a HIT, or the tree warmup (which draws effects
// behind the screen) is off. On a MISS the screen stays for the tree warmup.
internal static class DsDeferredPreload
{
    private const string PatchesType = "STS2Mobile.Patches.LifecycleAndPerformancePatches";
    private const string ScreenType = "STS2Mobile.Patches.AndroidStartupLoadingScreen";
    private static readonly TimeSpan CommonWaitLimit = TimeSpan.FromSeconds(60);

    private static Func<bool> _originalCommonPreload, _treeWarmupEnabled;
    private static Func<string> _vfxMode, _treeScope;
    private static Func<int> _treeFrames;
    private static object _noScreen;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var patches = AccessTools.TypeByName(PatchesType);
            var screen = AccessTools.TypeByName(ScreenType);
            if (patches == null || screen == null)
            {
                DsLog.Info("deferred preload: compat preload not found; left as is");
                return;
            }
            var showScreen = AccessTools.Method(patches, "ShowDeferredPreloadScreenAsync", new[] { typeof(Godot.Node) });
            var serialized = AccessTools.Method(patches, "RunSerializedAndroidPreloadAsync", new[] { screen });
            _originalCommonPreload = Getter<bool>(patches, "ShouldUseOriginalCommonMainMenuPreload");
            _treeWarmupEnabled = Getter<bool>(patches, "IsVfxTreeWarmupEnabled");
            _vfxMode = Getter<string>(patches, "GetVfxWarmupMode");
            _treeScope = Getter<string>(patches, "GetVfxTreeWarmupScope");
            _treeFrames = Getter<int>(patches, "GetVfxTreeWarmupFrames");

            if (serialized != null && _originalCommonPreload != null)
            {
                harmony.Patch(serialized, prefix: new HarmonyMethod(typeof(DsDeferredPreload), nameof(SerializedPrefix)));
                DsLog.Info("deferred preload: patched RunSerializedAndroidPreloadAsync");
            }
            if (showScreen != null && _treeWarmupEnabled != null && _vfxMode != null && _treeScope != null && _treeFrames != null)
            {
                _noScreen = AccessTools.Method(typeof(Task), nameof(Task.FromResult))
                    .MakeGenericMethod(screen)
                    .Invoke(null, new object[] { null });
                harmony.Patch(showScreen, prefix: new HarmonyMethod(typeof(DsDeferredPreload), nameof(ShowScreenPrefix)));
                DsLog.Info("deferred preload: patched ShowDeferredPreloadScreenAsync");
            }
        }
        catch (Exception e)
        {
            DsLog.Info($"deferred preload: patch failed: {e.Message}");
        }
    }

    private static Func<T> Getter<T>(Type type, string name)
    {
        var method = AccessTools.Method(type, name, Type.EmptyTypes);
        return method == null || method.ReturnType != typeof(T) ? null : method.CreateDelegate<Func<T>>();
    }

    private static bool ShowScreenPrefix(ref object __result)
    {
        try
        {
            string why;
            if (!_treeWarmupEnabled())
                why = "tree warmup off";
            else if (DsWarmupCache.Evaluate(_vfxMode(), _treeScope(), _treeFrames()).hit)
                why = "warmup cache hit";
            else
                return true;
            DsLog.Info($"deferred preload: no loading screen over the menu ({why})");
            __result = _noScreen;
            return false;
        }
        catch (Exception e)
        {
            DsLog.Info($"deferred preload: keeping the loading screen: {e.Message}");
            return true;
        }
    }

    private static bool SerializedPrefix(ref Task __result)
    {
        try
        {
            if (!_originalCommonPreload())
                return true;
            DsLog.Info("deferred preload: second Common pass skipped; waiting for the game's own");
            __result = WaitForCommon();
            return false;
        }
        catch (Exception e)
        {
            DsLog.Info($"deferred preload: running the second Common pass: {e.Message}");
            return true;
        }
    }

    private static async Task WaitForCommon()
    {
        var started = DateTime.UtcNow;
        var done = await Task.WhenAny(DsStartup.CommonPreloadTask, Task.Delay(CommonWaitLimit));
        if (done != DsStartup.CommonPreloadTask)
            DsLog.Info($"deferred preload: Common still loading after {CommonWaitLimit.TotalSeconds:0} s; warming up anyway");
        else
            DsLog.Info($"deferred preload: waited {(DateTime.UtcNow - started).TotalMilliseconds:0} ms for the game's Common preload");
    }
}
