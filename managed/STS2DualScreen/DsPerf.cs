using System;
using Godot;

namespace STS2DualScreen;

// Frame accounting for the 10 s stats line: what the lower screen did in
// each game frame, counted from one ProcessFrame to the next (that span is
// the frame time DsBottomScreen measures), and what the engine compiled.
internal static class DsPerf
{
    // This frame so far.
    private static int _callbacks;
    private static int _pileRebuilds;

    // This stats window.
    private static int _callbacksMax;
    private static ulong _callbackUsecMax, _presentUsecMax;
    private static int _pileRebuildTotal;

    // Pipeline compilations are cumulative counts; the line shows what was
    // added since the last one. -1: not read yet.
    private static long _canvas = -1, _draw = -1, _spec = -1;

    /// <summary>All of one capture callback (OnFrameData), in microseconds.</summary>
    public static void NoteCallback(ulong usec)
    {
        _callbacks++;
        _callbackUsecMax = Math.Max(_callbackUsecMax, usec);
    }

    /// <summary>All of one post-draw present, in microseconds.</summary>
    public static void NotePresent(ulong usec) => _presentUsecMax = Math.Max(_presentUsecMax, usec);

    public static void NotePileRebuild()
    {
        _pileRebuilds++;
        _pileRebuildTotal++;
    }

    /// <summary>A game frame ended (ProcessFrame again): fold its counts into the window.</summary>
    public static void EndFrame()
    {
        _callbacksMax = Math.Max(_callbacksMax, _callbacks);
        _callbacks = 0;
        _pileRebuilds = 0;
    }

    private static long Monitor(Performance.Monitor monitor)
    {
        try
        {
            return (long)Performance.GetMonitor(monitor);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string CompileDelta(ref long last, Performance.Monitor monitor)
    {
        var now = Monitor(monitor);
        var delta = last < 0 ? 0 : now - last;
        last = now;
        return delta.ToString();
    }

    public static string TakeStats()
    {
        var line = $"compiles canvas=+{CompileDelta(ref _canvas, Performance.Monitor.PipelineCompilationsCanvas)}"
            + $" draw=+{CompileDelta(ref _draw, Performance.Monitor.PipelineCompilationsDraw)}"
            + $" spec=+{CompileDelta(ref _spec, Performance.Monitor.PipelineCompilationsSpecialization)}"
            + $" captures_cb_max={_callbacksMax} cb_ms_max={_callbackUsecMax / 1000.0:F1}ms present_ms_max={_presentUsecMax / 1000.0:F1}ms"
            + $" pile_rebuilds={_pileRebuildTotal}";
        _callbacksMax = 0;
        _callbackUsecMax = _presentUsecMax = 0;
        _pileRebuildTotal = 0;
        return line;
    }
}
