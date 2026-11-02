using System;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>Started by the entry shim right after the compat pack's Apply().</summary>
public static class DsEntry
{
    private static bool _started;
    internal static DsConfig Config;
    internal static Harmony Harmony;

    public static void Init()
    {
        if (_started)
            return;
        _started = true;
        Config = DsConfig.Load();
        // Particles are thinned on one screen too; Attach skips the rest.
        Callable.From(Attach).CallDeferred();
        Harmony = new Harmony("dev.sts2ds.dualscreen");
        // Startup optimizations, restyled loading screen, and animation safety net
        // run on single- and dual-screen configs alike.
        DsStartup.Apply(Harmony);
        DsWarmupCache.Apply(Harmony);
        DsStartupLoadingScreenStyle.Apply(Harmony);
        DsAnimWarmup.Apply(Harmony);
        DsShakeGuard.Apply(Harmony);

        if (!Config.Enabled)
        {
            DsLog.Info("bottom screen disabled in settings; startup patches active");
            return;
        }

        // Patches go in now, before the game creates its UI; the bottom-screen
        // host is created once there is a SceneTree to attach it to.
        DsGameHooks.Apply(Harmony);
        DsLog.Info($"started (fps={Config.Fps}, tab={Config.DefaultTab})");
    }

    private static void Attach()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            Callable.From(Attach).CallDeferred();
            return;
        }
        DsParticles.Attach(tree, Config.ParticleAmount);
        if (!Config.Enabled)
            return;
        try
        {
            DsBottomScreen.Create(tree, Config);
        }
        catch (Exception e)
        {
            DsLog.Info($"bottom screen failed: {e}");
        }
    }
}
