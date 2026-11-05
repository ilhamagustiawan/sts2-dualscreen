using System;
using Godot;

namespace STS2DualScreen;

// Info lines (status, perf stats, hitches) only in the dev build: libsts2ds
// reports whether it was built with them (ds_verbose). Warnings always go out,
// to logcat and the game log, so a release build's failures stay readable.
public static class DsLog
{
    private static bool? _verbose;

    public static bool Verbose => _verbose ??= ReadVerbose();

    public static void Info(string message)
    {
        if (Verbose)
            Write(0, message);
    }

    public static void Warn(string message) => Write(1, message);

    private static bool ReadVerbose()
    {
        try
        {
            return DsNative.Verbose();
        }
        catch (Exception)
        {
            // An older libsts2ds without ds_verbose: keep logging.
            return true;
        }
    }

    private static void Write(int level, string message)
    {
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
