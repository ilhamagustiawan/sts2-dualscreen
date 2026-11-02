using System;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

// Thins out the game's particle effects. The game has no quality setting for
// them, so every emitter the game adds emits a fraction of what it was made
// with: GPU emitters through amount_ratio, which takes effect without a
// restart; CPU emitters through amount, cut once, before their first frame.
// Runs with or without the second screen.
//
// The game adds nodes through two helpers: GodotTreeExtensions.AddChildSafely
// and AddSiblingSafely (some 480 call sites, against one plain AddChild, in
// v0.111.0). Effects come in through them too, the compat pack's pooled ones
// as well (its pool hands a node back to the game's own add). So the emitters
// are found from those calls: the node being added is already a managed
// object, and the emitters under it are found in one native search per
// emitter type, which hands back only the emitters. This replaces a
// SceneTree.NodeAdded handler that made a managed wrapper for every node
// entering the tree, game-wide, a few hundred per card draw. If the helpers
// can't be patched, it falls back to that handler.
internal static class DsParticles
{
    private const string Extensions = "MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions";
    private static readonly StringName Thinned = "ds_thinned";
    private static float _ratio = 1f;
    private static int _count;
    private static int _mainThread;
    private static bool _failed;

    public static void Attach(SceneTree tree, Harmony harmony, float ratio)
    {
        if (ratio >= 0.999f)
            return;
        _ratio = ratio;
        _mainThread = System.Environment.CurrentManagedThreadId;
        if (!Patch(harmony))
        {
            tree.NodeAdded += OnNodeAdded;
            DsLog.Info($"particles at {ratio:P0} (every node added; the game's add helpers were not found)");
            return;
        }
        // Whatever the game added before the patch went in.
        Thin(tree.Root);
        DsLog.Info($"particles at {ratio:P0}");
    }

    private static bool Patch(Harmony harmony)
    {
        try
        {
            var type = AccessTools.TypeByName(Extensions);
            var addChild = type == null ? null : AccessTools.Method(type, "AddChildSafely", new[] { typeof(Node), typeof(Node) });
            var addSibling = type == null ? null : AccessTools.Method(type, "AddSiblingSafely", new[] { typeof(Node), typeof(Node) });
            if (addChild == null)
                return false;
            var postfix = new HarmonyMethod(typeof(DsParticles), nameof(AddPostfix));
            harmony.Patch(addChild, postfix: postfix);
            if (addSibling != null)
                harmony.Patch(addSibling, postfix: postfix);
            return true;
        }
        catch (Exception e)
        {
            DsLog.Info($"particles: could not patch the game's add helpers: {e.Message}");
            return false;
        }
    }

    // Runs whether the add happened now or was deferred: thinning needs the
    // node, not its place in the tree.
    private static void AddPostfix(Node child)
    {
        if (_failed || child == null)
            return;
        if (System.Environment.CurrentManagedThreadId != _mainThread)
        {
            // Rare: the helper defers these to the main thread itself.
            Callable.From(() => Thin(child)).CallDeferred();
            return;
        }
        Thin(child);
    }

    /// <summary>Thins every emitter at or under <paramref name="root"/>; cheap for a node with no children.</summary>
    public static void Thin(Node root)
    {
        if (_failed || _ratio >= 0.999f || root == null)
            return;
        try
        {
            if (!GodotObject.IsInstanceValid(root))
                return;
            ThinOne(root);
            if (root.GetChildCount() == 0)
                return;
            ThinAll(root, "GPUParticles2D");
            ThinAll(root, "CPUParticles2D");
        }
        catch (Exception e)
        {
            _failed = true;
            DsLog.Info($"could not thin particles, leaving them be: {e.Message}");
        }
    }

    private static void ThinAll(Node root, string type)
    {
        // Empty pattern: match by type only, without comparing every name.
        var found = root.FindChildren("", type, true, false);
        // Freed now rather than left to the finalizer thread.
        using var array = (Godot.Collections.Array)found;
        for (var i = 0; i < found.Count; i++)
            ThinOne(found[i]);
    }

    private static void ThinOne(Node node)
    {
        switch (node)
        {
            case GpuParticles2D gpu:
                if (gpu.AmountRatio <= _ratio)
                    return;
                gpu.AmountRatio = _ratio;
                break;
            // Pooled and re-added, or reparented: must not be cut again.
            case CpuParticles2D cpu when !cpu.HasMeta(Thinned):
                cpu.SetMeta(Thinned, true);
                cpu.Amount = Math.Max(1, (int)MathF.Round(cpu.Amount * _ratio));
                break;
            default:
                return;
        }
        if (++_count == 1)
            DsLog.Info("first particle emitter thinned");
    }

    // Fallback only.
    private static void OnNodeAdded(Node node)
    {
        try
        {
            ThinOne(node);
        }
        catch (Exception e)
        {
            DsLog.Info($"could not thin particles, leaving them be: {e.Message}");
            ((SceneTree)Engine.GetMainLoop()).NodeAdded -= OnNodeAdded;
        }
    }
}
