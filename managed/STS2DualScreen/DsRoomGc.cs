using System;
using System.Diagnostics;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Collects garbage while the room transition still covers the screen.
/// NTransition.RoomFadeIn starts the reveal of a room that has just loaded:
/// the load's garbage is collected and its finalizers run there, a frame
/// no one sees, instead of as a pause later in the fight.
/// </summary>
internal static class DsRoomGc
{
    private const string TransitionType = "MegaCrit.Sts2.Core.Nodes.NTransition";

    public static void Apply(Harmony harmony)
    {
        if (!DsEntry.Config.RoomGc)
            return;

        try
        {
            var type = AccessTools.TypeByName(TransitionType);
            var fadeIn = type == null ? null : AccessTools.Method(type, "RoomFadeIn");
            if (fadeIn == null)
            {
                DsLog.Warn("room gc: NTransition.RoomFadeIn not found; collections left to the runtime");
                return;
            }

            harmony.Patch(fadeIn, prefix: new HarmonyMethod(typeof(DsRoomGc), nameof(CollectUnderCover)));
        }
        catch (Exception ex)
        {
            DsLog.Warn($"room gc: could not patch NTransition.RoomFadeIn: {ex.Message}");
        }
    }

    // Full blocking collection, then the finalizers it queued, so Godot
    // wrappers are released here on the game thread's watch, not later.
    private static void CollectUnderCover()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            DsFinalizerGuard.DrainAll();
            DsLog.Debug($"room gc: collected in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            DsLog.Warn($"room gc: collection failed: {ex.Message}");
        }
    }
}
