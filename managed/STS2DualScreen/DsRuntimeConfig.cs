using System;
using System.IO;
using System.Text.Json;

namespace STS2DualScreen;

// The runtime keys GamePrep writes into dualscreen.json next to DsConfig's:
// read separately so the bottom screen's config stays as it is.
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
        var path = Path.Combine(DsPaths.FilesDir, "dualscreen.json");
        try
        {
            if (!File.Exists(path))
                return config;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("build_time_ms", out var b) && b.TryGetInt64(out var ms))
                config.BuildTimeMs = ms;
            if (root.TryGetProperty("perf_hint", out var h) && (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False))
                config.PerfHint = h.GetBoolean();
        }
        catch (Exception ex)
        {
            DsLog.Info($"dualscreen.json runtime keys unreadable, using defaults: {ex.Message}");
        }
        return config;
    }
}
