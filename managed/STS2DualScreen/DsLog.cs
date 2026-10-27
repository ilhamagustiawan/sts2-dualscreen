using System;
using Godot;

namespace STS2DualScreen;

internal static class DsLog
{
    public static void Info(string message)
    {
        var line = $"[STS2DualScreen] {message}";
        try
        {
            DsNative.Log(line);
        }
        catch (Exception)
        {
            Console.Error.WriteLine(line);
        }
        try
        {
            GD.Print(line);
        }
        catch (Exception) { }
    }
}
