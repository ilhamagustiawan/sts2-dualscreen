using System;
using System.Threading.Tasks;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Brackets the compat pack's startup warmup ("Rendering combat effects..."),
/// which runs in full on every launch: a skipped warmup left effects to
/// compile, and preloads to race, in the first rooms. Screen shake is held
/// off while it runs, and the loading line names it until it ends.
/// </summary>
internal static class DsStartupWarmup
{
    private const string StepEffects = "Rendering combat effects";

    // Scenes loaded so far and in all, for the bar (one load per scene).
    private static Func<string, string[]> _scenePaths;
    private static int _scenesDone;
    private static int _scenesTotal;

    public static void Apply(Harmony harmony)
    {
        var screenType = AccessTools.TypeByName("STS2Mobile.Patches.AndroidStartupLoadingScreen");
        var warmupMethod = screenType == null ? null : AccessTools.Method(screenType, "WarmupCriticalScenesAsync");

        if (warmupMethod == null)
        {
            DsLog.Warn("startup warmup: AndroidStartupLoadingScreen.WarmupCriticalScenesAsync not found; left as is");
            return;
        }

        try
        {
            harmony.Patch(
                warmupMethod,
                prefix: new HarmonyMethod(typeof(DsStartupWarmup), nameof(WarmupCriticalScenesPrefix)),
                postfix: new HarmonyMethod(typeof(DsStartupWarmup), nameof(WarmupCriticalScenesPostfix)));

            // Per-scene progress; without it the bar holds at the step's start.
            var paths = AccessTools.Method(screenType, "GetWarmupScenePaths", new[] { typeof(string) });
            var load = AccessTools.Method(screenType, "LoadWarmupScene");
            if (paths == null || load == null)
            {
                DsLog.Warn("startup warmup: scene list not found; no per-scene progress");
                return;
            }
            _scenePaths = paths.CreateDelegate<Func<string, string[]>>();
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(DsStartupWarmup), nameof(LoadScenePrefix)));
        }
        catch (Exception ex)
        {
            DsLog.Warn($"startup warmup: failed to patch WarmupCriticalScenesAsync: {ex.Message}");
        }
    }

    [HarmonyPrefix]
    private static void WarmupCriticalScenesPrefix(string mode)
    {
        DsShakeGuard.Begin();
        _scenesDone = 0;
        _scenesTotal = _scenePaths?.Invoke(mode)?.Length ?? 0;
        DsLoadingDetail.SetStep(StepEffects, DsLoadingDetail.EffectsAt);
    }

    // Each scene the warmup loads moves the bar across the effects share.
    private static void LoadScenePrefix()
    {
        if (_scenesTotal <= 0)
        {
            return;
        }
        _scenesDone++;
        var done = Math.Min(1f, (float)_scenesDone / _scenesTotal);
        var span = DsLoadingDetail.EffectsEnd - DsLoadingDetail.EffectsAt;
        DsLoadingDetail.SetStep(StepEffects, DsLoadingDetail.EffectsAt + span * done);
    }

    [HarmonyPostfix]
    private static void WarmupCriticalScenesPostfix(Task __result)
    {
        if (__result == null)
        {
            DsShakeGuard.End();
            return;
        }

        __result.ContinueWith(_ =>
        {
            DsShakeGuard.End();
            DsLoadingDetail.SetStep(null);
        });
    }
}
