using System;
using System.IO;
using System.Text.Json;

namespace STS2DualScreen;

// Written by the launcher (GamePrep) to <files>/dualscreen.json before every launch.
internal sealed class DsConfig
{
    public bool Enabled = true;
    public int Fps = 30;
    public string DefaultTab = "map";
    public bool ShowTopHint = true;

    /// <summary>Bottom-screen canvas is the panel size times this, so the game's
    /// 1080p-designed UI keeps a readable scale on the 3.9" panel.</summary>
    public float CanvasScale = 1.3f;

    public static string FilesDir()
    {
        // <files>/.godot/mono/publish/arm64/STS2DualScreen.dll
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(DsConfig).Assembly.Location) ?? ".");
        return dir.Parent?.Parent?.Parent?.Parent?.FullName ?? AppContext.BaseDirectory;
    }

    public static DsConfig Load()
    {
        var config = new DsConfig();
        var path = Path.Combine(FilesDir(), "dualscreen.json");
        try
        {
            if (!File.Exists(path))
                return config;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var e))
                config.Enabled = e.GetBoolean();
            if (root.TryGetProperty("fps", out var f) && f.TryGetInt32(out var fps))
                config.Fps = Math.Clamp(fps, 10, 60);
            if (root.TryGetProperty("default_tab", out var t) && t.ValueKind == JsonValueKind.String)
                config.DefaultTab = t.GetString();
            if (root.TryGetProperty("show_top_hint", out var h))
                config.ShowTopHint = h.GetBoolean();
            if (root.TryGetProperty("canvas_scale", out var s) && s.TryGetSingle(out var scale))
                config.CanvasScale = Math.Clamp(scale, 1f, 2f);
        }
        catch (Exception ex)
        {
            DsLog.Info($"dualscreen.json unreadable, using defaults: {ex.Message}");
        }
        return config;
    }
}
