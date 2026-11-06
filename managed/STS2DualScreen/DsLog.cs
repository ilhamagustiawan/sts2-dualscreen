using System;
using Godot;

namespace STS2DualScreen;

// Values match LogLevel.kt and ds_log.h.
public enum DsLogLevel
{
    Warn = 0,
    Info = 1,
    Debug = 2,
}

// Each level includes the ones before it. libsts2ds is built per build type
// with its level (ds_log_threshold), which this follows: release logs Warn,
// devel adds Info (milestones, decisions), debug adds Debug (per-event
// detail, perf stats). Warnings go to logcat and the game log in every build,
// so a release build's failures stay readable.
public static class DsLog
{
    private static DsLogLevel? _threshold;

    public static DsLogLevel Threshold => _threshold ??= ReadThreshold();

    public static bool Enabled(DsLogLevel level) => level <= Threshold;

    public static void Debug(string message) => Write(DsLogLevel.Debug, message);

    public static void Info(string message) => Write(DsLogLevel.Info, message);

    public static void Warn(string message) => Write(DsLogLevel.Warn, message);

    private static DsLogLevel ReadThreshold()
    {
        try
        {
            return (DsLogLevel)DsNative.LogThreshold();
        }
        catch (Exception)
        {
            // No libsts2ds to ask: keep logging what the dev build would.
            return DsLogLevel.Info;
        }
    }

    private static void Write(DsLogLevel level, string message)
    {
        if (!Enabled(level))
            return;

        var line = $"[STS2DualScreen] {message}";
        try
        {
            DsNative.Log(level, line);
        }
        catch (Exception)
        {
            Console.Error.WriteLine(line);
        }
        try
        {
            // Into godot.log, which the launcher's Log screen shows.
            GD.Print(line);
        }
        catch (Exception) { }
    }
}
