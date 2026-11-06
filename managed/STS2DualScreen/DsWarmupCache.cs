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
            DsLog.Warn("warmup cache: AndroidStartupLoadingScreen.WarmupCriticalScenesAsync not found; cache disabled");
            return;
        }

        try
        {
            harmony.Patch(
                warmupMethod,
                prefix: new HarmonyMethod(typeof(DsWarmupCache), nameof(WarmupCriticalScenesPrefix)),
                postfix: new HarmonyMethod(typeof(DsWarmupCache), nameof(WarmupCriticalScenesPostfix)));
        }
        catch (Exception ex)
        {
            DsLog.Warn($"warmup cache: failed to patch WarmupCriticalScenesAsync: {ex.Message}");
        }
    }

    [HarmonyPrefix]
    private static void WarmupCriticalScenesPrefix(
        string mode,
        ref bool treeWarmup,
        int treeWarmupFrames,
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
        int treeWarmupFrames,
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

            // A warmup that finished on another build says nothing about
            // this one. RecordSuccess writes a new stamp after every warmup
            // that finishes, so the launch after the first warmup on a new
            // build is judged by the checks below.
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

            // The shader cache alone says nothing about pipelines: Godot
            // writes its Vulkan pipeline cache only every so much growth or
            // on a clean exit, which Android seldom gives it. Skipping the
            // warmup without one leaves every effect to compile in the first
            // fights.
            if (UsesVulkan())
            {
                var (pipelineBytes, pipelineTime) = GetPipelineCacheStats();
                if (pipelineBytes == 0)
                    return (false, "no Vulkan pipeline cache on disk");
                // A warmup finished on this build with this much cache on
                // disk. The file is rewritten only when pipelines are added,
                // so after an update that compiles none it keeps its old
                // date: its size, not its age, is what tells.
                if (root.TryGetProperty("pipeline_cache_bytes", out var pb) && pb.TryGetInt64(out var stampBytes))
                {
                    if (pipelineBytes < stampBytes)
                        return (false, "Vulkan pipeline cache smaller than after the last warmup");
                }
                else
                {
                    // A stamp from before that was recorded: the cache must
                    // at least be from after the build was installed.
                    var buildTime = DsRuntimeConfig.Current.BuildTimeMs;
                    if (buildTime > 0 && pipelineTime < DateTimeOffset.FromUnixTimeMilliseconds(buildTime).UtcDateTime)
                        return (false, "Vulkan pipeline cache is older than the game build");
                }
            }

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
            var pipelineBytes = GetPipelineCacheStats().bytes;
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
                // What Evaluate holds the next launch's pipeline cache to.
                ["pipeline_cache_bytes"] = pipelineBytes,
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(stampPath, json);
            DsLog.Debug($"warmup cache: recorded stamp ({shaderCount} shaders, {shaderBytes} bytes, {pipelineBytes} pipeline cache bytes)");
        }
        catch (Exception ex)
        {
            DsLog.Warn($"warmup cache: could not record stamp: {ex.Message}");
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

    // Android reports no driver info (an empty list), so a driver update
    // went unnoticed. The pipeline cache UUID changes with the driver, as
    // the engine itself discards its cache on a change.
    private static string GetDriverInfo()
    {
        try
        {
            var uuid = RenderingServer.GetRenderingDevice()?.GetDevicePipelineCacheUuid();
            if (!string.IsNullOrEmpty(uuid))
            {
                return uuid;
            }

            var info = OS.GetVideoAdapterDriverInfo();
            var joined = info == null ? "" : string.Join(";", info);
            return joined.Length > 0 ? joined : "unknown";
        }
        catch { return "unknown"; }
    }
}
