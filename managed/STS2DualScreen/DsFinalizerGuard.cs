using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Runs the finalizer's release of Godot objects on the game thread.
///
/// A C# wrapper is finalized on Mono's finalizer thread: ~GodotObject calls
/// Dispose(false), which drops the wrapper's reference to the native object
/// (godotsharp_internal_refcounted_disposed). The engine does that in two
/// steps, unreference() and then a lookup in the object's instance bindings
/// under its mutex. Between them the game thread can drop the last native
/// reference and free the object, and the finalizer then locks a destroyed
/// mutex: "FORTIFY: pthread_mutex_lock called on a destroyed mutex", the
/// process aborting with Mono printing "Unreferenced static string" lines.
/// Fast play makes it likely: every card creates tweens, effects and
/// resources for the finalizer to release while the game thread frees them.
///
///   finalizer thread               game thread
///   ~GodotObject                   -
///     unreference() -> not last    -
///     (object still alive)         drops the last reference, frees it
///     lock its mutex  -> abort
///
/// The wrapper is held back instead and released on the game thread, once a
/// frame (and under each room transition), where no free can overlap it.
/// Arrays, Dictionaries and Variants are held back too: their finalizers can
/// drop the last reference to a Resource and free it off the game thread.
///
/// No unit test: it needs the engine's native library to create and free
/// Godot objects. It was checked by play on a device only.
/// </summary>
internal static class DsFinalizerGuard
{
    private const long DrainBudgetMs = 1;
    private const long DrainAllBudgetMs = 250;
    private const int FinalizerWaitMs = 2000;

    // Finalizers that release a native value, not a wrapper. Their
    // Dispose(bool) is small and may be inlined, so Finalize is patched.
    private static readonly Type[] ValueTypes =
    {
        typeof(Godot.Collections.Array),
        typeof(Godot.Collections.Dictionary),
        AccessTools.Inner(typeof(Variant), "Disposer"),
    };

    private static readonly ConcurrentQueue<object> Pending = new();
    private static Action<GodotObject, bool> _dispose;
    private static int _gameThread;
    private static long _released;
    private static int _failures;

    public static void Apply(Harmony harmony)
    {
        if (!DsEntry.Config.MainThreadFinalize)
        {
            DsLog.Info("finalizer guard: off in settings");
            return;
        }

        try
        {
            var dispose = AccessTools.Method(typeof(GodotObject), "Dispose", new[] { typeof(bool) });
            if (dispose == null)
            {
                DsLog.Warn("finalizer guard: GodotObject.Dispose(bool) not found; finalizers left on their thread");
                return;
            }

            _dispose = AccessTools.MethodDelegate<Action<GodotObject, bool>>(dispose);
            harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(DsFinalizerGuard), nameof(Defer)));
            DsLog.Info("finalizer guard: releasing finalized Godot objects on the game thread");
        }
        catch (Exception ex)
        {
            DsLog.Warn($"finalizer guard: could not patch GodotObject.Dispose: {ex.Message}");
            return;
        }

        foreach (var type in ValueTypes)
        {
            PatchFinalize(harmony, type);
        }
    }

    // One type's finalizer; a failure leaves only that type on its thread.
    private static void PatchFinalize(Harmony harmony, Type type)
    {
        try
        {
            var finalize = type == null ? null : AccessTools.DeclaredMethod(type, "Finalize");
            if (finalize == null)
            {
                DsLog.Warn($"finalizer guard: {type?.Name ?? "type"} finalizer not found; left on its thread");
                return;
            }

            harmony.Patch(finalize, prefix: new HarmonyMethod(typeof(DsFinalizerGuard), nameof(DeferRelease)));
        }
        catch (Exception ex)
        {
            DsLog.Warn($"finalizer guard: could not patch {type.Name} finalizer: {ex.Message}");
        }
    }

    /// <summary>Called on the game thread, deferred from the main loop: starts the per-frame release.</summary>
    public static void Attach(SceneTree tree)
    {
        if (_dispose == null)
        {
            return;
        }

        Volatile.Write(ref _gameThread, Thread.CurrentThread.ManagedThreadId);
        tree.ProcessFrame += OnFrame;
    }

    /// <summary>
    /// GC.WaitForPendingFinalizers with a deadline. It has none of its own: a
    /// finalizer stuck on a native lock would hold the game thread for good.
    /// Returns false if the finalizers did not finish in time.
    /// </summary>
    public static bool WaitFinalizers()
    {
        var waiter = new Thread(GC.WaitForPendingFinalizers) { IsBackground = true };
        waiter.Start();
        if (waiter.Join(FinalizerWaitMs))
        {
            return true;
        }

        DsLog.Warn($"finalizers still running after {FinalizerWaitMs} ms; not waiting");
        return false;
    }

    /// <summary>Releases what is held back, up to a time limit. Game thread only.</summary>
    public static void DrainAll()
    {
        var before = Interlocked.Read(ref _released);
        Drain(DrainAllBudgetMs);
        var count = Interlocked.Read(ref _released) - before;
        if (count > 0)
        {
            DsLog.Info($"finalizer guard: released {count} held back objects");
        }
    }

    // Harmony prefix: false skips the original Dispose(bool).
    private static bool Defer(GodotObject __instance, bool disposing)
    {
        if (disposing)
        {
            return true;
        }

        // Not attached yet (startup), or already on the game thread.
        var gameThread = Volatile.Read(ref _gameThread);
        if (gameThread == 0 || Thread.CurrentThread.ManagedThreadId == gameThread)
        {
            return true;
        }

        try
        {
            Pending.Enqueue(__instance);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Harmony prefix on a value type's Finalize: false skips the release.
    private static bool DeferRelease(object __instance)
    {
        var gameThread = Volatile.Read(ref _gameThread);
        if (gameThread == 0 || Thread.CurrentThread.ManagedThreadId == gameThread)
        {
            return true;
        }

        try
        {
            Pending.Enqueue(__instance);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void OnFrame() => Drain(DrainBudgetMs);

    private static void Drain(long budgetMs)
    {
        var start = Stopwatch.GetTimestamp();
        while (Pending.TryDequeue(out var instance))
        {
            try
            {
                // A value's Dispose(bool) ignores the flag: Dispose() is the same release.
                if (instance is GodotObject godotObject)
                {
                    _dispose(godotObject, false);
                }
                else
                {
                    ((IDisposable)instance).Dispose();
                }
                Interlocked.Increment(ref _released);
            }
            catch (Exception ex)
            {
                // A few lines are enough to see it; one bad object must not stop the rest.
                if (Interlocked.Increment(ref _failures) <= 5)
                {
                    DsLog.Warn($"finalizer guard: release failed: {ex.Message}");
                }
            }

            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= budgetMs)
            {
                return;
            }
        }
    }
}
