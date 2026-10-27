using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace STS2Mobile;

// The engine's entry point, shimmed.
//
// The custom Godot engine opens STS2Mobile.dll and calls STS2Mobile.ModEntry:
// first InitializeGodotSharp (GodotSharp's native interop), then Apply
// (Harmony patches, before the game's own startup). This file is staged as
// STS2Mobile.dll but its assembly name is STS2DsEntry, so the Sts2MobileLauncher
// compat pack, shipped next to it as STS2AndroidPortCompat.dll, can be loaded
// into the default context under its own name, STS2Mobile. That matters:
// Harmony's generated patch wrappers reference the compat pack by assembly
// name, and an earlier version that loaded it into a separate context had
// every wrapper resolve to this shim instead and fail.
//
// The compat pack is used unmodified. After it has run, the dual-screen layer
// starts. A failure there is logged and leaves the game single-screen.
public static class ModEntry
{
    private const string CompatFileName = "STS2AndroidPortCompat.dll";
    private static Type _compatEntry;
    private static bool _applied;

    [UnmanagedCallersOnly]
    public static int InitializeGodotSharp(
        IntPtr godotDllHandle,
        IntPtr outManagedCallbacks,
        IntPtr unmanagedCallbacks,
        int unmanagedCallbacksSize
    )
    {
        try
        {
            var entry = CompatEntry();
            unsafe
            {
                var fn = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, int>)
                    EntryPointer(entry, "InitializeGodotSharp");
                return fn(godotDllHandle, outManagedCallbacks, unmanagedCallbacks, unmanagedCallbacksSize);
            }
        }
        catch (Exception e)
        {
            Log($"compat InitializeGodotSharp failed: {e}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    public static void Apply()
    {
        if (_applied)
            return;
        _applied = true;
        try
        {
            unsafe
            {
                var fn = (delegate* unmanaged<void>)EntryPointer(CompatEntry(), "Apply");
                fn();
            }
        }
        catch (Exception e)
        {
            Log($"compat Apply failed: {e}");
        }

        try
        {
            STS2DualScreen.DsEntry.Init();
        }
        catch (Exception e)
        {
            Log($"dual screen failed to start, continuing single-screen: {e}");
        }
    }

    private static Type CompatEntry()
    {
        if (_compatEntry != null)
            return _compatEntry;
        // Assembly.Location is empty on this engine; see DsPaths.
        var path = Path.Combine(STS2DualScreen.DsPaths.PublishDir, CompatFileName);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        _compatEntry = assembly.GetType("STS2Mobile.ModEntry", throwOnError: true);
        Log($"compat pack loaded: {assembly.FullName} from {path}");
        return _compatEntry;
    }

    private static IntPtr EntryPointer(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, name);
        return method.MethodHandle.GetFunctionPointer();
    }

    internal static void Log(string message) => Console.Error.WriteLine($"[STS2DualScreen] {message}");

}
