using System;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Safety net for the experimental creature animation warmup.
/// Writes a marker file while the animation warmup runs; if a fight crashes
/// during warmup, the marker remains on disk so the launcher can safely turn
/// the setting off on next boot and notify the user.
/// </summary>
internal static class DsAnimWarmup
{
    public const string CrashMarkerFile = "dualscreen_anim_warmup.running";

    public static void Apply(Harmony harmony)
    {
        var warmupType = AccessTools.TypeByName("STS2Mobile.Patches.CombatAnimationWarmupPatches");
        var warmupMethod = warmupType == null ? null : AccessTools.Method(warmupType, "WarmupCombatAnimationsAsync");

        if (warmupMethod == null)
        {
            DsLog.Info("anim warmup: CombatAnimationWarmupPatches.WarmupCombatAnimationsAsync not found; safety net disabled");
            return;
        }

        try
        {
            harmony.Patch(
                warmupMethod,
                prefix: new HarmonyMethod(typeof(DsAnimWarmup), nameof(WarmupCombatAnimationsPrefix)),
                postfix: new HarmonyMethod(typeof(DsAnimWarmup), nameof(WarmupCombatAnimationsPostfix)));
            DsLog.Info("anim warmup: safety net active (monitoring combat animation warmup)");
        }
        catch (Exception ex)
        {
            DsLog.Info($"anim warmup: could not patch WarmupCombatAnimationsAsync: {ex.Message}");
        }
    }

    [HarmonyPrefix]
    private static void WarmupCombatAnimationsPrefix(string mode)
    {
        // Hit-effect warmup runs here even with animation warmup off.
        DsShakeGuard.Begin();
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var markerPath = Path.Combine(DsPaths.FilesDir, CrashMarkerFile);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            DsLog.Info($"anim warmup: could not write crash marker: {ex.Message}");
        }
    }

    [HarmonyPostfix]
    private static void WarmupCombatAnimationsPostfix(Task __result, string mode)
    {
        if (__result == null)
            DsShakeGuard.End();
        else
            __result.ContinueWith(_ => DsShakeGuard.End());

        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            return;

        void CleanMarker()
        {
            try
            {
                var markerPath = Path.Combine(DsPaths.FilesDir, CrashMarkerFile);
                if (File.Exists(markerPath))
                    File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                DsLog.Info($"anim warmup: could not clean crash marker: {ex.Message}");
            }
        }

        if (__result == null)
        {
            CleanMarker();
            return;
        }

        __result.ContinueWith(_ => CleanMarker());
    }
}
