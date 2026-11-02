using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

// Startup, kept out of the game's way.
//
// Right after the main menu shows, the game preloads its 'Common' set (some
// 700 assets and 90 vfx scenes, 10-16 s on the Thor), and a run waits for it.
// Its placeholder sounds (res://debug_audio/*.mp3) are in that set, and an
// mp3 is costly to load: Godot indexes every frame of it for seeking. They
// are played seldom, so here they are left out of that preload and warmed
// into the game's cache afterwards, two at a time, in the background. One
// played before it is warm loads the game's usual way (a cache miss).
//
// The bottom screen also waits for that preload before loading its own
// menu art (CommonPreloaded), so the two don't compete for the loader.
internal static class DsStartup
{
    private const string CommonSession = "Common";
    private const string DebugAudio = "res://debug_audio/";
    private const int WarmConcurrency = 2;

    private static bool _hooked;
    private static readonly List<string> Deferred = new();
    private static readonly Queue<string> ToWarm = new();
    private static readonly List<string> Warming = new();
    private static object _cache;
    private static MethodInfo _setAsset, _containsKey;
    private static PropertyInfo _sessionTask;
    private static SceneTree _tree;
    private static ulong _warmStartUsec;
    private static int _warmed;

    private static readonly TaskCompletionSource CommonDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The game's Common preload has finished (or can't be watched).</summary>
    public static bool CommonPreloaded
    {
        get => CommonDone.Task.IsCompleted;
        private set { if (value) CommonDone.TrySetResult(); }
    }

    /// <summary>Completes with <see cref="CommonPreloaded"/>.</summary>
    public static Task CommonPreloadTask => CommonDone.Task;

    public static void Apply(Harmony harmony)
    {
        var cacheType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.AssetCache");
        var sessionType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.AssetLoadingSession");
        var createSession = cacheType == null ? null : AccessTools.Method(cacheType, "CreateSession");
        _sessionTask = sessionType == null ? null : AccessTools.Property(sessionType, "Task");
        _setAsset = cacheType == null ? null : AccessTools.Method(cacheType, "SetAsset");
        _containsKey = cacheType == null ? null : AccessTools.Method(cacheType, "ContainsKey");
        try
        {
            // The session's own task is what finishes with the preload (the
            // method that starts it returns as soon as it is under way).
            if (createSession == null || _sessionTask == null || !typeof(Task).IsAssignableFrom(_sessionTask.PropertyType))
            {
                CommonPreloaded = true;
                DsLog.Info("startup: Common preload can't be watched; nothing deferred");
                return;
            }
            _hooked = _setAsset != null;
            harmony.Patch(createSession,
                prefix: new HarmonyMethod(typeof(DsStartup), nameof(CreateSessionPrefix)),
                postfix: new HarmonyMethod(typeof(DsStartup), nameof(CreateSessionPostfix)));
            DsLog.Info(_hooked ? "startup: debug sounds deferred until after the Common preload" : "startup: watching the Common preload");
        }
        catch (Exception e)
        {
            CommonPreloaded = true;
            DsLog.Info($"startup hooks failed: {e.Message}");
        }
    }

    // The sounds come out of the Common session only; later sessions (a run,
    // an act) list them again and find them cached, or load any that aren't.
    private static void CreateSessionPrefix(object __instance, string name, ref IEnumerable<string> paths)
    {
        if (name != CommonSession || paths == null || !_hooked)
            return;
        try
        {
            _cache = __instance;
            var all = paths.ToList();
            var keep = new List<string>(all.Count);
            foreach (var path in all)
            {
                if (path.StartsWith(DebugAudio, StringComparison.Ordinal) && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    Deferred.Add(path);
                else
                    keep.Add(path);
            }
            paths = keep;
            if (Deferred.Count > 0)
                DsLog.Info($"startup: {Deferred.Count} debug sound(s) left out of the Common preload");
        }
        catch (Exception e)
        {
            DsLog.Info($"startup: could not split the Common preload: {e.Message}");
        }
    }

    private static void CreateSessionPostfix(string name, object __result)
    {
        if (name != CommonSession || CommonPreloaded)
            return;
        var started = Time.GetTicksUsec();
        if (__result == null || _sessionTask.GetValue(__result) is not Task task)
        {
            CommonPreloaded = true;
            return;
        }
        // Finishes on a worker or the main thread; carry on on the main one.
        task.ContinueWith(_ => Callable.From(() => OnCommonLoaded(started)).CallDeferred());
    }

    private static void OnCommonLoaded(ulong startedUsec)
    {
        CommonPreloaded = true;
        DsLog.Info($"startup: Common preload done after {(Time.GetTicksUsec() - startedUsec) / 1000} ms");
        if (Deferred.Count == 0 || Engine.GetMainLoop() is not SceneTree tree)
            return;
        foreach (var path in Deferred)
            ToWarm.Enqueue(path);
        Deferred.Clear();
        _tree = tree;
        _warmStartUsec = Time.GetTicksUsec();
        _warmed = 0;
        _tree.ProcessFrame += WarmStep;
    }

    // A couple of threaded loads at a time, so the menu stays smooth.
    private static void WarmStep()
    {
        try
        {
            for (var i = Warming.Count - 1; i >= 0; i--)
            {
                var path = Warming[i];
                var status = ResourceLoader.LoadThreadedGetStatus(path);
                if (status == ResourceLoader.ThreadLoadStatus.InProgress)
                    continue;
                Warming.RemoveAt(i);
                if (status == ResourceLoader.ThreadLoadStatus.Loaded && ResourceLoader.LoadThreadedGet(path) is { } resource)
                {
                    _setAsset.Invoke(_cache, new object[] { path, resource });
                    _warmed++;
                }
            }
            while (Warming.Count < WarmConcurrency && ToWarm.TryDequeue(out var next))
            {
                // Played (and so loaded) already: nothing to do.
                if (_containsKey?.Invoke(_cache, new object[] { next }) is true)
                    continue;
                if (ResourceLoader.LoadThreadedRequest(next, "", false, ResourceLoader.CacheMode.Reuse) == Error.Ok)
                    Warming.Add(next);
            }
            if (Warming.Count > 0 || ToWarm.Count > 0)
                return;
        }
        catch (Exception e)
        {
            DsLog.Info($"startup: warming debug sounds stopped: {e.InnerException?.Message ?? e.Message}");
            ToWarm.Clear();
            Warming.Clear();
        }
        _tree.ProcessFrame -= WarmStep;
        DsLog.Info($"startup: {_warmed} debug sound(s) warmed in {(Time.GetTicksUsec() - _warmStartUsec) / 1000} ms, after the Common preload");
    }
}
