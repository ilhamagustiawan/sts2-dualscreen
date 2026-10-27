using System;
using Godot;

namespace STS2DualScreen;

// Thins out the game's particle effects. The game has no quality setting for
// them, so every emitter that enters the tree emits a fraction of what it was
// made with: GPU emitters through amount_ratio, which takes effect without a
// restart; CPU emitters through amount, cut once, before their first frame.
// Runs with or without the second screen.
internal static class DsParticles
{
    private static readonly StringName Thinned = "ds_thinned";
    private static float _ratio = 1f;
    private static int _count;

    public static void Attach(SceneTree tree, float ratio)
    {
        if (ratio >= 0.999f)
            return;
        _ratio = ratio;
        tree.NodeAdded += OnNodeAdded;
        DsLog.Info($"particles at {ratio:P0}");
    }

    private static void OnNodeAdded(Node node)
    {
        try
        {
            switch (node)
            {
                case GpuParticles2D gpu:
                    gpu.AmountRatio = Math.Min(gpu.AmountRatio, _ratio);
                    break;
                // Re-entering the tree (a reparent) must not cut it again.
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
        catch (Exception e)
        {
            DsLog.Info($"could not thin particles, leaving them be: {e.Message}");
            ((SceneTree)Engine.GetMainLoop()).NodeAdded -= OnNodeAdded;
        }
    }
}
