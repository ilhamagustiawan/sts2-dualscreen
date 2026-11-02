using System;
using Godot;

namespace STS2DualScreen;

// Tween property paths, made once. A string path becomes a new NodePath (a
// finalizable native object) on every TweenProperty call, and a burst of
// card or badge motion makes dozens of them.
internal static class DsNodePaths
{
    public static readonly NodePath Position = "position";
    public static readonly NodePath PositionY = "position:y";
    public static readonly NodePath Size = "size";
    public static readonly NodePath Scale = "scale";
    public static readonly NodePath Rotation = "rotation";
    public static readonly NodePath Modulate = "modulate";
    public static readonly NodePath ModulateA = "modulate:a";
    public static readonly NodePath ShaderS = "shader_parameter/s";
    public static readonly NodePath ShaderV = "shader_parameter/v";
}

internal enum DsCurve : byte
{
    Linear,
    CubicOut,
    ExpoOut,
    BackOut,
}

// Godot's tween curves (scene/animation/easing_equations.h), for motion
// stepped from managed code: progress 0..1 in, eased 0..1 out.
internal static class DsEase
{
    private const float BackOvershoot = 1.70158f;

    public static float Apply(DsCurve curve, float x)
    {
        switch (curve)
        {
            case DsCurve.CubicOut:
            {
                var t = x - 1f;
                return t * t * t + 1f;
            }
            case DsCurve.ExpoOut:
                return x >= 1f ? 1f : 1.001f * (1f - MathF.Pow(2f, -10f * x));
            case DsCurve.BackOut:
            {
                var t = x - 1f;
                return t * t * ((BackOvershoot + 1f) * t + BackOvershoot) + 1f;
            }
            default:
                return x;
        }
    }
}
