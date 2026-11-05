using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Godot;
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
    private const string PreviewType = "STS2Mobile.Patches.SpineWarmupPreview";

    // Creature scenes whose clips were warmed this session. The compat pack
    // warms every creature of every fight; after the first, its pipelines and
    // code are hot (and the Vulkan pipeline cache keeps them across sessions).
    private static readonly HashSet<string> Warmed = new();

    // A Spine warmup ran this fight, so its garbage is drained before the
    // room's mask lifts (DrainBeforeUnmask).
    private static bool _spineWarmed;

    public static void Apply(Harmony harmony)
    {
        var warmupType = AccessTools.TypeByName("STS2Mobile.Patches.CombatAnimationWarmupPatches");
        var warmupMethod = warmupType == null ? null : AccessTools.Method(warmupType, "WarmupCombatAnimationsAsync");

        if (warmupMethod == null)
        {
            DsLog.Warn("anim warmup: CombatAnimationWarmupPatches.WarmupCombatAnimationsAsync not found; safety net disabled");
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
            DsLog.Warn($"anim warmup: could not patch WarmupCombatAnimationsAsync: {ex.Message}");
        }

        PatchPreviewCreate(harmony);
        PatchMaskRemoval(harmony, warmupType);
    }

    // The warmup leaves dozens of short-lived Spine wrappers behind. Left to
    // the finalizer thread, they are released at some later GC, from that
    // thread, while the game thread runs Godot code. With the warmup on, the
    // game froze about a second after one, every thread waiting on a lock;
    // this is the likely cause. Draining them here makes that release happen
    // while the game thread waits in managed code, under the mask.
    private static void PatchMaskRemoval(Harmony harmony, Type warmupType)
    {
        var remove = AccessTools.Method(warmupType, "RemoveWarmupMask");
        if (remove == null)
        {
            DsLog.Info("anim warmup: no RemoveWarmupMask; warmup garbage left to the finalizer thread");
            return;
        }

        try
        {
            harmony.Patch(remove, prefix: new HarmonyMethod(typeof(DsAnimWarmup), nameof(DrainBeforeUnmask)));
            DsLog.Info("anim warmup: warmup garbage drained before the mask lifts");
        }
        catch (Exception ex)
        {
            DsLog.Warn($"anim warmup: could not patch RemoveWarmupMask: {ex.Message}");
        }
    }

    private static void DrainBeforeUnmask()
    {
        if (!_spineWarmed)
            return;
        _spineWarmed = false;

        var watch = Stopwatch.StartNew();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        DsLog.Info($"anim warmup: drained warmup garbage in {watch.ElapsedMilliseconds} ms");
    }

    // Compat packs from 450082d build an isolated SpineWarmupPreview per
    // creature; older ones don't have it, and warm every fight as before.
    private static void PatchPreviewCreate(Harmony harmony)
    {
        var previewType = AccessTools.TypeByName(PreviewType);
        var create = previewType == null ? null : AccessTools.Method(previewType, "Create", new[] { typeof(Node2D) });
        if (create == null)
        {
            DsLog.Info("anim warmup: no SpineWarmupPreview.Create; every fight warms all creatures");
            return;
        }

        try
        {
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(DsAnimWarmup), nameof(PreviewCreatePrefix)));
            DsLog.Info("anim warmup: each skeleton warmed once per session");
        }
        catch (Exception ex)
        {
            DsLog.Warn($"anim warmup: could not patch SpineWarmupPreview.Create: {ex.Message}");
        }
    }

    // A creature already warmed: no preview (null), so the compat loop skips
    // it. Anything unexpected runs the original.
    private static bool PreviewCreatePrefix(Node2D source)
    {
        try
        {
            var key = CreatureKey(source);
            return key == null || Warmed.Add(key);
        }
        catch (Exception ex)
        {
            DsLog.Warn($"anim warmup: skeleton check failed, warming anyway: {ex.Message}");
            return true;
        }
    }

    // The creature's scene, e.g. "res://scenes/creatures/.../seapunk.tscn":
    // the sprite's owner. Read from the node tree only, so no Spine object
    // gets a managed wrapper here. No scene path: always warmed.
    private static string CreatureKey(Node2D source)
    {
        if (source == null || !GodotObject.IsInstanceValid(source))
            return null;
        var path = source.Owner?.SceneFilePath;
        if (string.IsNullOrEmpty(path))
            path = source.SceneFilePath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    [HarmonyPrefix]
    private static void WarmupCombatAnimationsPrefix(string mode)
    {
        // Hit-effect warmup runs here even with animation warmup off.
        DsShakeGuard.Begin();
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            return;
        _spineWarmed = true;

        try
        {
            var markerPath = Path.Combine(DsPaths.FilesDir, CrashMarkerFile);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            DsLog.Warn($"anim warmup: could not write crash marker: {ex.Message}");
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
                DsLog.Warn($"anim warmup: could not clean crash marker: {ex.Message}");
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
