using System;
using System.IO;
using System.Text.Json;

namespace STS2DualScreen;

// The runtime keys GamePrep writes into dualscreen.json next to DsConfig's:
// read separately so the bottom screen's config stays as it is. Like
// DsConfig, dualscreen_overrides.json, if there, is laid over it.
internal sealed class DsRuntimeConfig
{
    private static DsRuntimeConfig _current;

    /// <summary>When the installed game build was written (newest of the PCK and sts2.dll), epoch ms; 0 if unknown.</summary>
    public long BuildTimeMs;

    /// <summary>Report frame work to Android's performance hint (ADPF).</summary>
    public bool PerfHint = true;

    public static DsRuntimeConfig Current => _current ??= Load();

    private static DsRuntimeConfig Load()
    {
        var config = new DsRuntimeConfig();
        config.Read(Path.Combine(DsPaths.FilesDir, "dualscreen.json"));
        // DsConfig logs that the overrides file was applied.
        config.Read(Path.Combine(DsPaths.FilesDir, DsConfig.OverridesFile));
        return config;
    }

    private void Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("build_time_ms", out var b) && b.TryGetInt64(out var ms))
                BuildTimeMs = ms;
            if (root.TryGetProperty("perf_hint", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False)
                PerfHint = h.GetBoolean();
        }
        catch (Exception ex)
        {
            DsLog.Info($"{Path.GetFileName(path)} runtime keys unreadable, using defaults for them: {ex.Message}");
        }
    }
}
