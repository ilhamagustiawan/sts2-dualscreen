using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

// Subtext under the game's "Loading..." (NLoadingOverlay) saying what the
// startup is doing, e.g. "Loading assets 312/790".
//
//   Loading...            <- the game's label
//   Loading assets 312/790  <- this
//
// While the Common preload runs, the line follows that session's own
// counters. Afterwards the steps that follow report themselves with SetStep.
//
// Progress (0..1) for the boot cover's bar: the Common preload fills the
// first share, each later step reports where it is past that.
//
//   0 ── Common assets ── 0.6 ─ code 0.65 ─ effects 0.7..0.9 ─ 0.92 ─ 0.96 ─ 1
internal static class DsLoadingDetail
{
    private const string OverlayType = "MegaCrit.Sts2.Core.Nodes.CommonUi.NLoadingOverlay";
    private const string CodePrewarmType = "STS2Mobile.Patches.AndroidStartupLoadingScreen";
    private const string CodePrewarmMethod = "PrewarmCombatHotPaths";
    private const string StepCombatCode = "Compiling combat code";
    private const string LabelName = "DsLoadingDetail";
    private const string GameLabelPath = "%Label";
    private const int FontSize = 22;
    private const float GapPx = 8f;

    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] PendingQueues = { "_toLoad", "_loading", "_finalizing", "_vfxScenes" };

    /// <summary>Where each startup step starts on the bar.</summary>
    public const float CommonShare = 0.6f;
    public const float CodeAt = 0.65f;
    public const float EffectsAt = 0.7f;
    public const float EffectsEnd = 0.9f;
    public const float GameplayAt = 0.92f;
    public const float MenusAt = 0.96f;
    /// <summary>No progress known.</summary>
    public const float Unknown = -1f;

    private static object _session;
    private static volatile string _step;
    private static float _stepProgress = Unknown;
    private static Label _detail;
    private static SceneTree _tree;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var overlay = AccessTools.TypeByName(OverlayType);
            var ready = overlay == null ? null : AccessTools.Method(overlay, "_Ready");
            if (ready == null)
            {
                DsLog.Warn("loading detail: NLoadingOverlay._Ready not found; no subtext");
                return;
            }

            harmony.Patch(ready, postfix: new HarmonyMethod(typeof(DsLoadingDetail), nameof(ReadyPostfix)));

            var screen = AccessTools.TypeByName(CodePrewarmType);
            var prewarm = screen == null ? null : AccessTools.Method(screen, CodePrewarmMethod);
            if (prewarm != null)
            {
                harmony.Patch(prewarm, prefix: new HarmonyMethod(typeof(DsLoadingDetail), nameof(PrewarmPrefix)));
            }

            DsLog.Info("loading detail: subtext under the loading label");
        }
        catch (Exception e)
        {
            DsLog.Warn($"loading detail: patch failed: {e.Message}");
        }
    }

    /// <summary>The game's Common preload session; its counters drive the line.</summary>
    public static void WatchSession(object session)
    {
        _session = session;
    }

    /// <summary>What the line says now: the session's counters, else the step; "" for none.</summary>
    public static string Text => Describe();

    /// <summary>The bar's fill for the line above; Unknown before the session starts.</summary>
    public static float Progress
    {
        get
        {
            if (_session != null && !DsStartup.CommonPreloaded)
            {
                var (loaded, total) = Counters(_session);
                return total > 0 ? CommonShare * loaded / total : 0f;
            }
            return _stepProgress;
        }
    }

    /// <summary>A step outside that session; null clears it. Progress stays where it was unless given.</summary>
    public static void SetStep(string text, float progress = Unknown)
    {
        _step = text;
        if (progress >= 0f)
        {
            _stepProgress = progress;
        }
    }

    private static void PrewarmPrefix()
    {
        SetStep(StepCombatCode, CodeAt);
    }

    private static void ReadyPostfix(Control __instance)
    {
        try
        {
            var label = __instance.GetNodeOrNull<Control>(GameLabelPath);
            if (label == null)
            {
                DsLog.Warn("loading detail: overlay has no %Label");
                return;
            }

            _detail = Build(label);
            _tree = __instance.GetTree();
            _tree.ProcessFrame += Tick;
        }
        catch (Exception e)
        {
            DsLog.Warn($"loading detail: could not add subtext: {e.Message}");
        }
    }

    // A plain label under the game's, dressed like it. In a box container it
    // just follows the label; elsewhere it is placed against the label's box.
    private static Label Build(Control gameLabel)
    {
        var detail = new Label
        {
            Name = LabelName,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0.75f),
        };
        detail.AddThemeFontSizeOverride("font_size", FontSize);
        if (gameLabel.GetThemeFont("font") is { } font)
        {
            detail.AddThemeFontOverride("font", font);
        }

        var parent = gameLabel.GetParent();
        parent.AddChild(detail);
        if (parent is BoxContainer)
        {
            parent.MoveChild(detail, gameLabel.GetIndex() + 1);
            return detail;
        }

        detail.Position = gameLabel.Position + new Vector2(0f, gameLabel.Size.Y + GapPx);
        detail.Size = new Vector2(gameLabel.Size.X, detail.Size.Y);
        return detail;
    }

    private static void Tick()
    {
        if (!GodotObject.IsInstanceValid(_detail))
        {
            _tree.ProcessFrame -= Tick;
            _detail = null;
            return;
        }

        var text = Describe();
        if (_detail.Text != text)
        {
            _detail.Text = text;
        }
    }

    private static string Describe()
    {
        if (_session != null && !DsStartup.CommonPreloaded)
        {
            return DescribeSession(_session);
        }

        return _step ?? "";
    }

    private static string DescribeSession(object session)
    {
        var (loaded, total) = Counters(session);
        return total > 0 ? $"Loading assets {loaded:N0}/{total:N0}" : "";
    }

    // The session has no public counters; its queues hold what is left.
    private static (int loaded, int total) Counters(object session)
    {
        var loaded = ReadInt(session, "_totalLoaded");
        var left = 0;
        foreach (var queue in PendingQueues)
        {
            left += CountOf(session, queue);
        }

        return (loaded, loaded + left);
    }

    private static int ReadInt(object target, string field)
    {
        return target.GetType().GetField(field, Fields)?.GetValue(target) is int value ? value : 0;
    }

    private static int CountOf(object target, string field)
    {
        return target.GetType().GetField(field, Fields)?.GetValue(target) is System.Collections.ICollection items ? items.Count : 0;
    }
}
