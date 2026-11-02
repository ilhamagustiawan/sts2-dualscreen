using System;
using Godot;

namespace STS2DualScreen;

// Frame accounting for the 10 s stats line and the hitch log: what the
// lower screen did in each game frame, counted from one ProcessFrame to the
// next (that span is the frame time DsBottomScreen measures), and what the
// engine compiled or collected in it. A slow frame logs one line with all of
// it, so a shader compile, a JIT or GC pause and the lower screen's own work
// can be told apart.
internal static class DsPerf
{
    private const ulong HitchUsec = 20_000;
    // The hitch log can't spam: at most one line this often, and so many a
    // stats window; the rest are only counted.
    private const ulong HitchGapUsec = 250_000;
    private const int HitchLinesPerWindow = 20;

    // This frame so far.
    private static int _callbacks;
    private static ulong _callbackUsec, _presentUsec;
    private static int _pileRebuilds, _tweens;
    private static long _frameCompiles = -1;
    private static int _frameGcMinor, _frameGcMajor;

    private static ulong _lastHitchLineUsec;
    private static int _hitchLines, _hitchLinesDropped;

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
        _callbackUsec += usec;
        _callbackUsecMax = Math.Max(_callbackUsecMax, usec);
    }

    /// <summary>All of one post-draw present, in microseconds.</summary>
    public static void NotePresent(ulong usec)
    {
        _presentUsec += usec;
        _presentUsecMax = Math.Max(_presentUsecMax, usec);
    }

    /// <summary>A tween was started on the lower screen (DsStyle.Animate/Loop).</summary>
    public static void NoteTween() => _tweens++;

    public static void NotePileRebuild()
    {
        _pileRebuilds++;
        _pileRebuildTotal++;
    }

    /// <summary>
    /// A game frame ended (ProcessFrame again, dtUsec after the last): its
    /// counts go into the window, and into a hitch line if it was slow.
    /// </summary>
    public static void EndFrame(ulong now, ulong dtUsec, string tab, bool combat)
    {
        var compiles = Monitor(Performance.Monitor.PipelineCompilationsCanvas)
            + Monitor(Performance.Monitor.PipelineCompilationsDraw)
            + Monitor(Performance.Monitor.PipelineCompilationsSpecialization);
        var gcMinor = GC.CollectionCount(0);
        var gcMajor = GC.CollectionCount(GC.MaxGeneration);
        if (dtUsec > HitchUsec && _frameCompiles >= 0)
        {
            if (_hitchLines >= HitchLinesPerWindow || now - _lastHitchLineUsec < HitchGapUsec)
            {
                _hitchLinesDropped++;
            }
            else
            {
                _hitchLines++;
                _lastHitchLineUsec = now;
                DsLog.Info($"hitch {dtUsec / 1000.0:F1}ms: compiles={compiles - _frameCompiles} gc_minor={gcMinor - _frameGcMinor} gc_major={gcMajor - _frameGcMajor}"
                    + $" captures_cb={_callbacks} cb_ms={_callbackUsec / 1000.0:F1} present_ms={_presentUsec / 1000.0:F1}"
                    + $" pile_rebuilds={_pileRebuilds} tweens={_tweens} tab={tab} combat={combat}");
            }
        }
        _frameCompiles = compiles;
        _frameGcMinor = gcMinor;
        _frameGcMajor = gcMajor;
        _callbacksMax = Math.Max(_callbacksMax, _callbacks);
        _callbacks = _pileRebuilds = _tweens = 0;
        _callbackUsec = _presentUsec = 0;
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
            + $" pile_rebuilds={_pileRebuildTotal} hitch_lines={_hitchLines} hitch_lines_dropped={_hitchLinesDropped}";
        _hitchLines = _hitchLinesDropped = 0;
        _callbacksMax = 0;
        _callbackUsecMax = _presentUsecMax = 0;
        _pileRebuildTotal = 0;
        return line;
    }
}
