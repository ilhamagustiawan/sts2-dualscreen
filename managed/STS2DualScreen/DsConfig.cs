using System;
using System.IO;
using System.Text.Json;

namespace STS2DualScreen;

// Written by the launcher (GamePrep) to <files>/dualscreen.json before every
// launch; <files>/dualscreen_overrides.json, if there, is laid over it.
internal sealed class DsConfig
{
    public bool Enabled = true;
    public int Fps = 30;
    public string DefaultTab = "map";

    /// <summary>Bottom-screen canvas is the panel size times this, so the game's
    /// 1080p-designed UI keeps a readable scale on the 3.9" panel.</summary>
    public float CanvasScale = 1.3f;

    /// <summary>Pile page rate in a fight while its cards move (the still rate is 15).</summary>
    public int CombatMotionFps = 15;

    /// <summary>Pages not shown don't process (their game nodes' _Process and tweens).</summary>
    public bool PauseHiddenPages = true;

    /// <summary>Collect garbage under each room transition (DsRoomGc).</summary>
    public bool RoomGc = true;

    /// <summary>Release finalized Godot objects on the game thread, not Mono's finalizer thread (DsFinalizerGuard).</summary>
    public bool MainThreadFinalize = true;

    /// <summary>Grow the renderer's texture upload pool before the first preload (DsUploadWarmup).</summary>
    public bool UploadWarmup = true;

    /// <summary>At most one lower-screen part renders, and one is copied out, a game frame.</summary>
    public bool StaggerCaptures = true;

    /// <summary>
    /// libsts2ds compares, composes and posts frames on its own thread; the
    /// game's thread only copies each one over. Off: all of it in the game's frame.
    /// </summary>
    public bool PresentThread = true;

    /// <summary>Share of each particle emitter's particles that are drawn; 1 is all.</summary>
    public float ParticleAmount = 1f;

    /// <summary>Game payload stamp: dllSha:pckStamp.</summary>
    public string Build = "";

    /// <summary>Compatibility pack identifier: packId/targetId.</summary>
    public string Compat = "";

    public static DsConfig Load()
    {
        var config = new DsConfig();
        config.Read(Path.Combine(DsPaths.FilesDir, "dualscreen.json"));
        // The launcher rewrites dualscreen.json on every launch. Switches it
        // has no setting for (yet) go here, by hand, same keys; they win.
        if (config.Read(Path.Combine(DsPaths.FilesDir, OverridesFile)))
            DsLog.Info($"{OverridesFile} applied");
        return config;
    }

    public const string OverridesFile = "dualscreen_overrides.json";

    private bool Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var e))
                Enabled = e.GetBoolean();
            if (root.TryGetProperty("fps", out var f) && f.TryGetInt32(out var fps))
                Fps = Math.Clamp(fps, 10, 60);
            if (root.TryGetProperty("combat_motion_fps", out var cm) && cm.TryGetInt32(out var combatMotion))
                CombatMotionFps = Math.Clamp(combatMotion, 10, 60);
            if (root.TryGetProperty("pause_hidden_pages", out var ph) && ph.ValueKind is JsonValueKind.True or JsonValueKind.False)
                PauseHiddenPages = ph.GetBoolean();
            if (root.TryGetProperty("room_gc", out var rg) && rg.ValueKind is JsonValueKind.True or JsonValueKind.False)
                RoomGc = rg.GetBoolean();
            if (root.TryGetProperty("main_thread_finalize", out var mf) && mf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                MainThreadFinalize = mf.GetBoolean();
            if (root.TryGetProperty("upload_warmup", out var uw) && uw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                UploadWarmup = uw.GetBoolean();
            if (root.TryGetProperty("stagger_captures", out var sc) && sc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                StaggerCaptures = sc.GetBoolean();
            if (root.TryGetProperty("present_thread", out var pt) && pt.ValueKind is JsonValueKind.True or JsonValueKind.False)
                PresentThread = pt.GetBoolean();
            if (root.TryGetProperty("default_tab", out var t) && t.ValueKind == JsonValueKind.String)
                DefaultTab = t.GetString();
            if (root.TryGetProperty("particle_amount", out var p) && p.TryGetSingle(out var amount))
                ParticleAmount = Math.Clamp(amount, 0.1f, 1f);
            if (root.TryGetProperty("canvas_scale", out var s) && s.TryGetSingle(out var scale))
                CanvasScale = Math.Clamp(scale, 1f, 2f);
            if (root.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.String)
                Build = b.GetString() ?? "";
            if (root.TryGetProperty("compat", out var c) && c.ValueKind == JsonValueKind.String)
                Compat = c.GetString() ?? "";
            return true;
        }
        catch (Exception ex)
        {
            DsLog.Warn($"{Path.GetFileName(path)} unreadable, using defaults for it: {ex.Message}");
            return false;
        }
    }
}
