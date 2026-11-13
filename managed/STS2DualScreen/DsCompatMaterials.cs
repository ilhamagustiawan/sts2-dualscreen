using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// The launcher forces the compat pack's shader compatibility mode off, but
/// its in-game switch can turn it on for a session. On, the compat pack swaps a
/// CanvasItem's ShaderMaterial for a copy that runs its mobile shader, one
/// deferred call after the node enters the tree. A game script that took the
/// node's material in its <c>_Ready</c> keeps the old one and sets uniforms
/// on a material nothing draws with. The crystal ball of the Scrying event
/// (the fortune teller) is one: its <c>time</c> and per-cell fade uniforms
/// never reach the screen, so the ball stays on its idle swirl and the
/// reveal never shows. The compat pack repairs such fields only for the
/// main menu, epoch slots, the radial blur and cards.
///
/// Here every swap the compat pack makes is followed by a look at the node
/// and its ancestors: an instance field still holding the old material is
/// pointed at the new one. Only frames with a swap do any work beyond a list
/// copy; the field list of each type is found once.
/// </summary>
internal static class DsCompatMaterials
{
    private const string PatchesType = "STS2Mobile.Patches.ShaderCompatibilityPatches";

    private static AccessTools.FieldRef<List<CanvasItem>> _pending;
    private static readonly List<CanvasItem> Batch = new();
    private static readonly List<(ShaderMaterial Old, ShaderMaterial New)> Swaps = new();
    private static readonly Dictionary<Type, FieldInfo[]> FieldsByType = new();
    private static readonly HashSet<string> Logged = new();
    private static bool _applying;
    private static bool _failed;

    public static void Apply(Harmony harmony)
    {
        var type = AccessTools.TypeByName(PatchesType);
        var apply = type == null ? null : AccessTools.Method(type, "ApplyPendingMaterials");
        var create = type == null ? null : AccessTools.Method(type, "TryCreateReplacement");
        var pending = type == null ? null : AccessTools.Field(type, "PendingMaterials");
        if (apply == null || create == null || pending?.FieldType != typeof(List<CanvasItem>))
        {
            DsLog.Info("compat materials: no shader compatibility swap found; nothing to rebind");
            return;
        }
        try
        {
            _pending = AccessTools.StaticFieldRefAccess<List<CanvasItem>>(pending);
            harmony.Patch(apply,
                prefix: new HarmonyMethod(typeof(DsCompatMaterials), nameof(ApplyPrefix)),
                finalizer: new HarmonyMethod(typeof(DsCompatMaterials), nameof(ApplyFinalizer)));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(DsCompatMaterials), nameof(CreatePostfix)));
            DsLog.Info("compat materials: game fields follow swapped shader materials");
        }
        catch (Exception e)
        {
            DsLog.Warn($"compat materials: could not patch the shader swap: {e.Message}");
        }
    }

    // The compat pack clears its pending list when it is done, so the batch
    // it is about to go through is copied first (references only).
    private static void ApplyPrefix()
    {
        if (_failed)
            return;
        Batch.Clear();
        Swaps.Clear();
        var pending = _pending();
        if (pending == null || pending.Count == 0)
            return;
        Batch.AddRange(pending);
        _applying = true;
    }

    // Also called for the compat pack's own cached-field repairs (menu, cards,
    // the energy counter); those set their fields themselves, so only swaps
    // made inside ApplyPendingMaterials are noted.
    private static void CreatePostfix(bool __result, ShaderMaterial source, ref ShaderMaterial replacement)
    {
        if (_applying && __result && source != null && replacement != null)
            Swaps.Add((source, replacement));
    }

    // A finalizer so the flag and the batch are cleared even if the compat
    // pack throws; returns null to leave its exception (if any) as it was.
    private static Exception ApplyFinalizer(Exception __exception)
    {
        _applying = false;
        try
        {
            if (Swaps.Count > 0)
                Rebind();
        }
        catch (Exception e)
        {
            _failed = true;
            DsLog.Warn($"compat materials: rebinding failed, left as is: {e.Message}");
        }
        finally
        {
            Batch.Clear();
            Swaps.Clear();
        }
        return __exception;
    }

    private static void Rebind()
    {
        foreach (var item in Batch)
        {
            if (!GodotObject.IsInstanceValid(item) || item.Material is not ShaderMaterial now)
                continue;
            var old = OldFor(now);
            if (old == null)
                continue;
            for (Node node = item; node != null; node = node.GetParent())
                RebindFields(node, old, now);
        }
    }

    private static ShaderMaterial OldFor(ShaderMaterial now)
    {
        foreach (var (o, n) in Swaps)
            if (ReferenceEquals(n, now))
                return o;
        return null;
    }

    private static void RebindFields(Node node, ShaderMaterial old, ShaderMaterial now)
    {
        foreach (var field in FieldsOf(node.GetType()))
        {
            if (!ReferenceEquals(field.GetValue(node), old))
                continue;
            field.SetValue(node, now);
            var key = $"{field.DeclaringType?.Name}.{field.Name}";
            if (Logged.Add(key))
                DsLog.Info($"compat materials: {key} now holds {now.Shader?.ResourcePath}");
        }
    }

    // Instance fields that can hold a ShaderMaterial, declared by the game
    // (or a mod), not by GodotSharp: Godot's own types keep no such fields.
    private static FieldInfo[] FieldsOf(Type type)
    {
        if (FieldsByType.TryGetValue(type, out var cached))
            return cached;
        var godot = typeof(Node).Assembly;
        var found = new List<FieldInfo>();
        for (var t = type; t != null && t.Assembly != godot; t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsAssignableFrom(typeof(ShaderMaterial)))
                    found.Add(f);
            }
        }
        var fields = found.ToArray();
        FieldsByType[type] = fields;
        return fields;
    }
}
