using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Caches the startup warmup result (compiled shader cache on disk).
/// On repeat launches when nothing has changed, it skips the heavy tree warmup
/// ("Rendering combat effects...") while keeping the scene asset preload
/// ("Compiling combat effects..."), cutting startup time down to ~1 second.
/// On a HIT that runs with no loading screen at all (see DsDeferredPreload).
/// </summary>
internal static class DsWarmupCache
{
    private const string StampFileName = "dualscreen_warmup.json";

    public static bool IsHit { get; private set; }

    public static void Apply(Harmony harmony)
    {
        var screenType = AccessTools.TypeByName("STS2Mobile.Patches.AndroidStartupLoadingScreen");
        var warmupMethod = screenType == null ? null : AccessTools.Method(screenType, "WarmupCriticalScenesAsync");

        if (warmupMethod == null)
        {
            DsLog.Info("warmup cache: AndroidStartupLoadingScreen.WarmupCriticalScenesAsync not found; cache disabled");
            return;
        }

        try
        {
            harmony.Patch(
                warmupMethod,
                prefix: new HarmonyMethod(typeof(DsWarmupCache), nameof(WarmupCriticalScenesPrefix)),
                postfix: new HarmonyMethod(typeof(DsWarmupCache), nameof(WarmupCriticalScenesPostfix)));
            DsLog.Info("warmup cache: patched WarmupCriticalScenesAsync");
        }
        catch (Exception ex)
        {
            DsLog.Info($"warmup cache: failed to patch WarmupCriticalScenesAsync: {ex.Message}");
        }
    }

    [HarmonyPrefix]
    private static void WarmupCriticalScenesPrefix(
        object loadingScreen,
        string mode,
        ref bool treeWarmup,
        int treeWarmupFrames,
        bool retainCache,
        string treeWarmupScope)
    {
        DsShakeGuard.Begin();
        if (!treeWarmup)
        {
            IsHit = false;
            return;
        }

        var (hit, reason) = Evaluate(mode, treeWarmupScope, treeWarmupFrames);
        IsHit = hit;

        if (hit)
        {
            DsLog.Info("warmup cache: HIT — skipping tree draw, keeping scene asset preload");
            treeWarmup = false;
        }
        else
        {
            DsLog.Info($"warmup cache: MISS ({reason}) — running full tree warmup");
        }
    }

    [HarmonyPostfix]
    private static void WarmupCriticalScenesPostfix(
        Task __result,
        string mode,
        bool treeWarmup,
        int treeWarmupFrames,
        bool retainCache,
        string treeWarmupScope)
    {
        if (__result == null)
        {
            DsShakeGuard.End();
            return;
        }

        __result.ContinueWith(t =>
        {
            DsShakeGuard.End();
            if (t.IsCompletedSuccessfully)
            {
                RecordSuccess(mode, treeWarmupScope, treeWarmupFrames);
            }
        });
    }

    public static (bool hit, string reason) Evaluate(string mode, string treeWarmupScope, int treeWarmupFrames)
    {
        var stampPath = Path.Combine(DsPaths.FilesDir, StampFileName);
        if (!File.Exists(stampPath))
            return (false, "no stamp file");

        var config = DsEntry.Config;
        if (string.IsNullOrEmpty(config?.Build))
            return (false, "build info unavailable");

        var (shaderCount, shaderBytes) = GetShaderCacheStats();
        if (shaderCount == 0 || shaderBytes == 0)
            return (false, "shader_cache is empty or missing");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stampPath));
            var root = doc.RootElement;

            if (!root.TryGetProperty("build", out var b) || b.GetString() != config.Build)
                return (false, "game build changed");

            if (!root.TryGetProperty("compat", out var c) || c.GetString() != config.Compat)
                return (false, "compat target changed");

            var adapter = GetVideoAdapter();
            if (!root.TryGetProperty("adapter", out var a) || a.GetString() != adapter)
                return (false, "GPU adapter changed");

            var driver = GetDriverInfo();
            if (!root.TryGetProperty("driver", out var d) || d.GetString() != driver)
                return (false, "GPU driver changed");

            if (!root.TryGetProperty("vfx_mode", out var vm) || vm.GetString() != mode)
                return (false, "vfx mode changed");

            if (!root.TryGetProperty("vfx_scope", out var vs) || vs.GetString() != treeWarmupScope)
                return (false, "vfx scope changed");

            if (!root.TryGetProperty("vfx_frames", out var vf) || vf.GetInt32() != treeWarmupFrames)
                return (false, "vfx frames changed");

            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, $"stamp unreadable: {ex.Message}");
        }
    }

    private static void RecordSuccess(string mode, string treeWarmupScope, int treeWarmupFrames)
    {
        try
        {
            var (shaderCount, shaderBytes) = GetShaderCacheStats();
            var stampPath = Path.Combine(DsPaths.FilesDir, StampFileName);
            var config = DsEntry.Config;

            var data = new Dictionary<string, object>
            {
                ["build"] = config?.Build ?? "",
                ["compat"] = config?.Compat ?? "",
                ["adapter"] = GetVideoAdapter(),
                ["driver"] = GetDriverInfo(),
                ["vfx_mode"] = mode ?? "",
                ["vfx_scope"] = treeWarmupScope ?? "",
                ["vfx_frames"] = treeWarmupFrames,
                ["shader_cache_files"] = shaderCount,
                ["shader_cache_bytes"] = shaderBytes,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(stampPath, json);
            DsLog.Info($"warmup cache: recorded stamp ({shaderCount} shaders, {shaderBytes} bytes)");
        }
        catch (Exception ex)
        {
            DsLog.Info($"warmup cache: could not record stamp: {ex.Message}");
        }
    }

    private static (int count, long bytes) GetShaderCacheStats()
    {
        var path = Path.Combine(DsPaths.FilesDir, "shader_cache");
        if (!Directory.Exists(path))
            return (0, 0);

        int count = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                count++;
                try { bytes += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return (count, bytes);
    }

    private static string GetVideoAdapter()
    {
        try { return RenderingServer.GetVideoAdapterName() ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string GetDriverInfo()
    {
        try
        {
            var info = OS.GetVideoAdapterDriverInfo();
            return info != null ? string.Join(";", info) : "unknown";
        }
        catch { return "unknown"; }
    }
}
