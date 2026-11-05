using System;
using System.Threading;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// The compat pack's warmups instantiate real attack VFX (NHeavyBluntVfx,
/// NShivThrowVfx, ...) out of sight, and those call NGame.ScreenShake from
/// their play sequence, so the loading screen visibly shakes near the end of
/// startup. Shake, rumble, trauma and hit-stop are dropped while a warmup runs
/// and for a short grace period after, since some effects shake on a delay.
/// </summary>
internal static class DsShakeGuard
{
    private const long GraceMs = 1500;

    private static int _active;
    private static long _quietUntil;

    public static bool Suppressing =>
        Volatile.Read(ref _active) > 0 || Environment.TickCount64 < Interlocked.Read(ref _quietUntil);

    public static void Apply(Harmony harmony)
    {
        var game = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");
        if (game == null)
        {
            DsLog.Warn("shake guard: NGame not found; warmup shakes not suppressed");
            return;
        }
        var prefix = new HarmonyMethod(typeof(DsShakeGuard), nameof(Prefix));
        var patched = 0;
        foreach (var name in new[] { "ScreenShake", "ScreenRumble", "ScreenShakeTrauma", "DoHitStop" })
        {
            var method = AccessTools.Method(game, name);
            if (method == null)
                continue;
            try
            {
                harmony.Patch(method, prefix: prefix);
                patched++;
            }
            catch (Exception ex)
            {
                DsLog.Warn($"shake guard: could not patch NGame.{name}: {ex.Message}");
            }
        }
        DsLog.Info($"shake guard: patched {patched} NGame shake method(s)");
    }

    public static void Begin() => Interlocked.Increment(ref _active);

    public static void End()
    {
        Interlocked.Exchange(ref _quietUntil, Environment.TickCount64 + GraceMs);
        Interlocked.Decrement(ref _active);
    }

    private static bool Prefix() => !Suppressing;
}
