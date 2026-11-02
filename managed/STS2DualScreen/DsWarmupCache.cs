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

        // The shader cache alone says nothing about pipelines: Godot writes
        // its Vulkan pipeline cache only every few MB of growth or on a clean
        // exit, which Android seldom gives it. Skipping the warmup without one
        // leaves every effect to compile in the first fights.
        if (UsesVulkan())
        {
            var (pipelineBytes, pipelineTime) = GetPipelineCacheStats();
            if (pipelineBytes == 0)
                return (false, "no Vulkan pipeline cache on disk");
            var buildTime = DsRuntimeConfig.Current.BuildTimeMs;
            if (buildTime > 0 && pipelineTime < DateTimeOffset.FromUnixTimeMilliseconds(buildTime).UtcDateTime)
                return (false, "Vulkan pipeline cache is older than the game build");
        }

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
                ["pipeline_cache_bytes"] = GetPipelineCacheStats().bytes,
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

    private static bool UsesVulkan()
    {
        try { return RenderingServer.GetCurrentRenderingDriverName() == "vulkan"; }
        catch { return true; }
    }

    /// <summary>Total size and newest write of user://vulkan/pipelines*.cache.</summary>
    private static (long bytes, DateTime newestUtc) GetPipelineCacheStats()
    {
        long bytes = 0;
        var newest = DateTime.MinValue;
        try
        {
            var dir = Path.Combine(OS.GetUserDataDir(), "vulkan");
            if (!Directory.Exists(dir))
                return (0, newest);
            foreach (var file in Directory.EnumerateFiles(dir, "pipelines*.cache"))
            {
                var info = new FileInfo(file);
                if (info.Length == 0)
                    continue;
                bytes += info.Length;
                if (info.LastWriteTimeUtc > newest)
                    newest = info.LastWriteTimeUtc;
            }
        }
        catch { }
        return (bytes, newest);
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
