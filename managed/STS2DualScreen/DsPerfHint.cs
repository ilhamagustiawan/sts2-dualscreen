using System;
using System.Runtime.InteropServices;
using Godot;

namespace STS2DualScreen;

// Android's performance hint (ADPF, API 33+) for the game thread: once per
// frame the thread's CPU time is reported against a target of one frame at
// the current refresh rate and FPS cap (perf_hint.c). With it, the CPU clocks
// rise as soon as a burst of work starts (a hand of cards, a big effect)
// rather than after the frames it costs. A launcher setting (perf_hint in
// dualscreen.json), on by default; a no-op where the device has no ADPF or
// anything fails. Runs with or without the second screen.
internal static class DsPerfHint
{
    private const string Lib = "libsts2ds.so";
    private const int RecheckFrames = 120;

    [DllImport(Lib, EntryPoint = "ds_perf_start")]
    private static extern int Start(long targetNs);

    [DllImport(Lib, EntryPoint = "ds_perf_target")]
    private static extern void Target(long targetNs);

    [DllImport(Lib, EntryPoint = "ds_perf_frame")]
    private static extern int Frame();

    private static SceneTree _tree;
    private static long _targetNs;
    private static int _frames;

    public static void Attach(SceneTree tree)
    {
        if (!DsRuntimeConfig.Current.PerfHint)
            return;
        try
        {
            // Called deferred from the main loop: this is the game thread.
            _targetNs = FrameTargetNs();
            if (Start(_targetNs) == 0)
            {
                DsLog.Warn("perf hint: not available; nothing reported");
                return;
            }
            _tree = tree;
            _tree.ProcessFrame += OnFrame;
            DsLog.Info($"perf hint: on, target {_targetNs / 1e6:F2} ms per frame");
        }
        catch (Exception e)
        {
            DsLog.Warn($"perf hint: could not start: {e.Message}");
        }
    }

    private static void OnFrame()
    {
        try
        {
            if (Frame() == 0)
            {
                _tree.ProcessFrame -= OnFrame;
                return;
            }
            // The FPS cap can change in the game's settings, the panel's rate
            // with the refresh setting.
            if (++_frames < RecheckFrames)
                return;
            _frames = 0;
            var target = FrameTargetNs();
            if (Math.Abs(target - _targetNs) > 500_000)
            {
                _targetNs = target;
                Target(target);
                DsLog.Debug($"perf hint: target now {target / 1e6:F2} ms");
            }
        }
        catch (Exception e)
        {
            _tree.ProcessFrame -= OnFrame;
            DsLog.Warn($"perf hint: stopped: {e.Message}");
        }
    }

    /// <summary>One frame at the slower of the panel's refresh rate and the game's FPS cap.</summary>
    private static long FrameTargetNs()
    {
        double fps = 60;
        try
        {
            var refresh = DisplayServer.ScreenGetRefreshRate();
            if (refresh > 1)
                fps = refresh;
        }
        catch (Exception) { }
        var cap = Engine.MaxFps;
        if (cap > 0 && cap < fps)
            fps = cap;
        return (long)(1e9 / Math.Clamp(fps, 20, 240));
    }
}
