using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace STS2Mobile;

// The engine's entry point, shimmed.
//
// The custom Godot engine binds to STS2Mobile.ModEntry by name: first
// InitializeGodotSharp (sets up GodotSharp's native interop), then Apply
// (Harmony patches, before the game's own startup). The Sts2MobileLauncher
// compat pack implements both, under the same assembly name, so it is shipped
// next to this file as STS2AndroidPortCompat.dll and loaded into a load
// context of its own; everything it references (GodotSharp, Harmony, sts2)
// still resolves to the default context, so it patches the same game.
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
        var dir = Path.GetDirectoryName(typeof(ModEntry).Assembly.Location) ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, CompatFileName);
        var context = new CompatLoadContext();
        var assembly = context.LoadFromAssemblyPath(path);
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

    // Everything except the compat pack itself comes from the default context.
    private sealed class CompatLoadContext : AssemblyLoadContext
    {
        public CompatLoadContext()
            : base("sts2-android-compat", isCollectible: false) { }

        protected override Assembly Load(AssemblyName assemblyName) => null;
    }
}
