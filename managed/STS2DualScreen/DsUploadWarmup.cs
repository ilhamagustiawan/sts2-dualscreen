using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Grows the renderer's texture upload pool to full size before the first preload.
///
/// Godot's RenderingDevice uploads textures through a pool of transfer
/// workers, one per CPU core, added on demand while every worker is busy.
/// Adding one races with threads reading the pool, and the game's preload
/// uploads from many threads at once (up to 128 loads in flight). On Android
/// that aborts the engine now and then, as the pool grows from one worker to two:
///
///   FATAL: Index p_index = 1 is out of bounds (count = 1)
///   CompressedTexture2D::load → RenderingDevice::texture_create (preload thread)
///
/// Here one thread per core uploads a small texture at the same moment, a few
/// rounds, while the main thread waits: the pool grows with nobody else
/// reading it, and later uploads only reuse its workers.
/// </summary>
internal static class DsUploadWarmup
{
    private const string SessionType = "MegaCrit.Sts2.Core.Assets.AssetLoadingSession";

    // 512² RGBA8 is 1 MB: each copy outlasts the other threads' start.
    private const int Side = 512;
    private const int Rounds = 4;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private static bool _done;

    public static void Apply(Harmony harmony)
    {
        if (!DsEntry.Config.UploadWarmup)
        {
            DsLog.Info("upload warmup: off in overrides");
            return;
        }

        try
        {
            var session = AccessTools.TypeByName(SessionType);
            var ctors = session == null ? new List<ConstructorInfo>() : AccessTools.GetDeclaredConstructors(session);
            if (ctors.Count == 0)
            {
                DsLog.Warn("upload warmup: AssetLoadingSession not found; the upload pool grows during preloads");
                return;
            }

            foreach (var ctor in ctors)
            {
                harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(DsUploadWarmup), nameof(SessionPrefix)));
            }
        }
        catch (Exception ex)
        {
            DsLog.Warn($"upload warmup: could not patch AssetLoadingSession: {ex.Message}");
        }
    }

    private static void SessionPrefix()
    {
        if (_done)
        {
            return;
        }

        // The main thread must be the one waiting: it reads the pool every frame.
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            return;
        }
        _done = true;

        try
        {
            Grow();
        }
        catch (Exception ex)
        {
            DsLog.Warn($"upload warmup: failed, the upload pool grows during preloads: {ex.Message}");
        }
    }

    private static void Grow()
    {
        var watch = Stopwatch.StartNew();
        int count = Math.Max(1, OS.GetProcessorCount());
        var textures = new List<Rid>();
        var start = new Barrier(count);

        // One thread per pool slot, released together each round.
        var threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            var image = Image.CreateEmpty(Side, Side, false, Image.Format.Rgba8);
            threads[i] = new Thread(() => Upload(image, start, textures)) { IsBackground = true };
            threads[i].Start();
        }

        bool finished = true;
        foreach (var thread in threads)
        {
            finished &= thread.Join(JoinTimeout);
        }
        if (!finished)
        {
            DsLog.Warn("upload warmup: threads did not finish; the upload pool may still grow during preloads");
            return;
        }

        // Only the pool's workers are kept; the textures go.
        foreach (var texture in textures)
        {
            RenderingServer.FreeRid(texture);
        }
        start.Dispose();
        DsLog.Info($"upload warmup: {count} threads x {Rounds} uploads in {watch.ElapsedMilliseconds} ms");
    }

    private static void Upload(Image image, Barrier start, List<Rid> textures)
    {
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                start.SignalAndWait(StartTimeout);
                var texture = RenderingServer.Texture2DCreate(image);
                lock (textures)
                {
                    textures.Add(texture);
                }
            }
        }
        catch (Exception ex)
        {
            DsLog.Warn($"upload warmup: upload failed: {ex.Message}");
        }
    }
}
