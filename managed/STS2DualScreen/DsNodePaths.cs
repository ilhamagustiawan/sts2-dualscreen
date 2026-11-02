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
