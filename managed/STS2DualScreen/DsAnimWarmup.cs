using System;
using System.Collections.Generic;
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

    // Skeletons whose clips were warmed this session. The compat pack warms
    // every creature of every fight; after the first, its pipelines and code
    // are hot (and the Vulkan pipeline cache keeps them across sessions).
    private static readonly HashSet<string> Warmed = new();

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

        PatchPreviewCreate(harmony);
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
            DsLog.Info($"anim warmup: could not patch SpineWarmupPreview.Create: {ex.Message}");
        }
    }

    // A skeleton already warmed: no preview (null), so the compat loop skips
    // the creature. Anything unexpected runs the original.
    private static bool PreviewCreatePrefix(Node2D source)
    {
        try
        {
            var key = SkeletonKey(source);
            return key == null || Warmed.Add(key);
        }
        catch (Exception ex)
        {
            DsLog.Info($"anim warmup: skeleton check failed, warming anyway: {ex.Message}");
            return true;
        }
    }

    // The skeleton data's resource path, e.g. "res://animations/.../x.tres".
    // The data object is the game's (a borrowed binding): read it, never free
    // it. Embedded data has no path and is always warmed.
    private static string SkeletonKey(Node2D source)
    {
        if (source == null || !GodotObject.IsInstanceValid(source))
            return null;
        using var data = source.Call("get_skeleton_data_res");
        var path = (data.AsGodotObject() as Resource)?.ResourcePath;
        return string.IsNullOrEmpty(path) ? null : path;
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
