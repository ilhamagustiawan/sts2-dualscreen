using System;
using System.Collections.Generic;
using Godot;

namespace STS2DualScreen;

// A set of tabs, drawn in its own small viewport so that its motion never
// costs a full-screen capture: the strip along the bottom edge (the combat
// piles), or a plate in a top corner (the map, the deck), as the game puts
// its own buttons. DsDockSet treats them as one dock.
//
// Each tab is the game's icon over its localized name, styled and moving as
// the game's own buttons do: the settings tabs' selected/unselected look
// (cream or half-cream text, the hsv shader's s and v), the top-bar buttons'
// press and elastic settle, the combat piles' count bump, and, forever while
// it applies, the top-bar map button's rocking when travel is possible and
// the deck button's rocking while its screen is open. A gold marker slides to
// the selected tab and breathes.
internal enum DockShape
{
    Strip,
    CornerLeft,
    CornerRight,
}

internal sealed class DsDock
{
    /// <summary>A corner plate's viewport, in canvas units: the plate and room for its shadow.</summary>
    public static readonly Vector2 CornerSize = new(200f, 162f);
    private const float CornerShadow = 12f;

    /// <summary>How far a corner plate reaches down the pages, in canvas units.</summary>
    public const float CornerPlateHeight = 162f - CornerShadow;

    private const float IconSize = 84f;
    private const float BadgeSize = 46f;
    private const float SelectedScale = 1.1f;
    private const float UnselectedScale = 0.95f;

    private static readonly Dictionary<string, (string Table, string Key, string Fallback)> TitleKeys = new()
    {
        ["map"] = ("static_hover_tips", "MAP.title", "Map"),
        ["draw"] = ("static_hover_tips", "DRAW_PILE.title", "Draw"),
        ["discard"] = ("static_hover_tips", "DISCARD_PILE.title", "Discard"),
        ["exhaust"] = ("static_hover_tips", "EXHAUST_PILE.title", "Exhaust"),
        ["deck"] = ("static_hover_tips", "DECK.title", "Deck"),
    };

    private sealed class Tab
    {
        public string Key;
        public Button Button;
        public Control Holder;   // state scale, press rotation
        public Control Rocker;   // forever rocking, count bump
        public TextureRect Icon;
        public ShaderMaterial Hsv;
        public Label Label;
        public Control Badge;
        public Label BadgeLabel;
        public Label BadgeOld;   // the number rolling out
        public string Title;
        public int Count = -1;   // the game's latest
        public int Shown = -1;   // what the badge shows (or is rolling to)
        public bool Dirty;
        public bool Grew;        // the tick under way: up and bump, or down and dip
        public bool Enabled = true;
        public Tween State, Press, Bump, Tick, Rock;
        public Callable TickStep, BadgeHidden;   // made once
    }

    private readonly Control _root;
    private readonly DockShape _shape;
    private readonly Action<string> _onPressed;
    private readonly Dictionary<string, Tab> _tabs = new();
    private readonly Panel _marker;
    private Tween _markerMove, _markerBreath;
    private string _selected;
    private bool _traveling;
    private bool _live;
    private readonly Callable _flushCounts;
    private bool _flushQueued;

    public DsDock(Control root, string[] tabs, Action<string> onPressed, DockShape shape = DockShape.Strip)
    {
        _root = root;
        _shape = shape;
        _onPressed = onPressed;
        _flushCounts = Callable.From(FlushCounts);

        var row = new HBoxContainer { Name = "Tabs", MouseFilter = Control.MouseFilterEnum.Pass };
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.AddThemeConstantOverride("separation", 8);
        if (shape == DockShape.Strip)
        {
            BuildStrip();
            row.OffsetLeft = 12;
            row.OffsetRight = -12;
            row.OffsetTop = 4;
        }
        else
        {
            var plate = BuildPlate(shape == DockShape.CornerLeft);
            row.OffsetLeft = plate.OffsetLeft + 6;
            row.OffsetRight = plate.OffsetRight - 6;
            row.OffsetBottom = plate.OffsetBottom - 10;
        }
        _root.AddChild(row);
        foreach (var key in tabs)
        {
            var tab = BuildTab(key);
            row.AddChild(tab.Button);
            _tabs[key] = tab;
        }

        var markerStyle = new StyleBoxFlat
        {
            BgColor = DsStyle.Gold,
            ShadowColor = DsStyle.Gold with { A = 0.55f },
            ShadowSize = 9,
        };
        markerStyle.SetCornerRadiusAll(3);
        _marker = new Panel { Name = "Marker", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _marker.AddThemeStyleboxOverride("panel", markerStyle);
        _marker.Size = new Vector2(0, 6);
        _root.AddChild(_marker);
        _root.Resized += () => PlaceMarker(animate: false);
    }

    // Opaque: it is posted on its own, not over the pages. Darker at the
    // bottom edge, with a faint gold rim and glow along the top like the
    // game's framed panels.
    private void BuildStrip()
    {
        var backdrop = new TextureRect
        {
            Name = "Backdrop",
            Texture = Gradient(new Color(0.11f, 0.095f, 0.08f), new Color(0.035f, 0.03f, 0.025f)),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(backdrop);
        var glow = new TextureRect
        {
            Name = "Glow",
            Texture = Gradient(DsStyle.Gold with { A = 0.16f }, DsStyle.Gold with { A = 0f }),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        glow.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        glow.OffsetBottom = 22;
        _root.AddChild(glow);
        var rim = new ColorRect { Name = "Rim", Color = DsStyle.Gold with { A = 0.45f }, MouseFilter = Control.MouseFilterEnum.Ignore };
        rim.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        rim.OffsetBottom = 2;
        _root.AddChild(rim);
    }

    // Over the pages, in a transparent viewport: the strip's colours on a
    // plate hung from the screen's corner, its free corner rounded and
    // rimmed in gold, with a soft shadow on the page.
    private Control BuildPlate(bool left)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.078f, 0.066f, 0.94f),
            BorderColor = DsStyle.Gold with { A = 0.45f },
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = (int)CornerShadow,
        };
        style.BorderWidthBottom = 2;
        style.BorderWidthLeft = left ? 0 : 2;
        style.BorderWidthRight = left ? 2 : 0;
        style.CornerRadiusBottomLeft = left ? 0 : 22;
        style.CornerRadiusBottomRight = left ? 22 : 0;
        var plate = new Panel { Name = "Plate", MouseFilter = Control.MouseFilterEnum.Ignore };
        plate.AddThemeStyleboxOverride("panel", style);
        plate.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        plate.OffsetBottom = -CornerShadow;
        plate.OffsetLeft = left ? 0 : CornerShadow;
        plate.OffsetRight = left ? -CornerShadow : 0;
        _root.AddChild(plate);
        return plate;
    }

    public bool Has(string key) => _tabs.ContainsKey(key);

    // The icons and the shader load when the dock first shows, not while
    // the game boots.
    private bool _assetsLoaded;

    private void LoadAssets()
    {
        if (_assetsLoaded)
            return;
        _assetsLoaded = true;
        foreach (var tab in _tabs.Values)
        {
            tab.Hsv = DsStyle.Hsv();
            tab.Icon.Material = tab.Hsv;
            ApplyState(tab, animate: false);
        }
        SetIcon("map", DsStyle.Icon(DsStyle.MapIcon));
        SetIcon("deck", DsStyle.Icon(DsStyle.DeckIcon));
        SetIcon("discard", DsStyle.Icon(DsStyle.DiscardIcon));
    }

    /// <summary>Something on the dock loops forever: capture it at the ambient rate.</summary>
    public bool Animating
    {
        get
        {
            if (!_live)
                return false;
            if (DsStyle.Running(_markerBreath) && _marker.Visible)
                return true;
            foreach (var tab in _tabs.Values)
            {
                if (DsStyle.Running(tab.Rock))
                    return true;
            }
            return false;
        }
    }

    public string Title(string key) => _tabs.TryGetValue(key, out var tab) ? tab.Title : key;

    public Texture2D IconOf(string key) => _tabs.TryGetValue(key, out var tab) ? tab.Icon.Texture : null;

    private Tab BuildTab(string key)
    {
        var tab = new Tab { Key = key, Title = TitleKeys[key].Fallback };
        var button = new Button
        {
            Name = $"Tab_{key}",
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            ClipText = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
            button.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        tab.Button = button;

        var holder = new Control { Name = "Holder", MouseFilter = Control.MouseFilterEnum.Ignore };
        holder.AnchorLeft = holder.AnchorRight = 0.5f;
        holder.OffsetLeft = -IconSize / 2;
        holder.OffsetRight = IconSize / 2;
        holder.OffsetTop = 6;
        holder.OffsetBottom = 6 + IconSize;
        holder.PivotOffset = new Vector2(IconSize / 2, IconSize / 2);
        holder.Scale = Vector2.One * UnselectedScale;
        button.AddChild(holder);
        tab.Holder = holder;

        var rocker = new Control { Name = "Rocker", MouseFilter = Control.MouseFilterEnum.Ignore };
        rocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rocker.PivotOffset = new Vector2(IconSize / 2, IconSize / 2);
        holder.AddChild(rocker);
        tab.Rocker = rocker;

        var icon = new TextureRect
        {
            Name = "Icon",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        icon.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rocker.AddChild(icon);
        tab.Icon = icon;

        var label = DsStyle.Text(34, 8);
        label.Name = "Label";
        label.ClipText = true;
        label.Text = tab.Title;
        label.Modulate = DsStyle.HalfCream;
        button.AddChild(label);
        tab.Label = label;

        if (key != "map")
        {
            var badgeStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.07f, 0.06f, 0.05f, 0.92f),
                BorderColor = DsStyle.Cream with { A = 0.7f },
            };
            badgeStyle.SetBorderWidthAll(3);
            badgeStyle.SetCornerRadiusAll((int)(BadgeSize / 2));
            var badge = new Panel { Name = "Badge", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            badge.AddThemeStyleboxOverride("panel", badgeStyle);
            badge.AnchorLeft = badge.AnchorRight = 0.5f;
            badge.OffsetLeft = IconSize / 2 - BadgeSize * 0.55f;
            badge.OffsetRight = badge.OffsetLeft + BadgeSize;
            badge.OffsetTop = 2;
            badge.OffsetBottom = 2 + BadgeSize;
            badge.PivotOffset = new Vector2(BadgeSize / 2, BadgeSize / 2);
            var old = DsStyle.Text(27, 6);
            old.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            old.Visible = false;
            badge.AddChild(old);
            var count = DsStyle.Text(27, 6);
            count.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            badge.AddChild(count);
            button.AddChild(badge);
            tab.Badge = badge;
            tab.BadgeLabel = count;
            tab.BadgeOld = old;
            tab.TickStep = Callable.From<float>(t => StepTick(tab, t));
            tab.BadgeHidden = Callable.From(() => OnBadgeHidden(tab));
        }

        button.ButtonDown += () => OnDown(tab);
        button.ButtonUp += () => OnUp(tab);
        button.Pressed += () =>
        {
            DsStyle.Click();
            _onPressed(key);
        };
        Layout(tab);
        return tab;
    }

    // Icon over the name; the name alone, larger, until there is an icon.
    private static void Layout(Tab tab)
    {
        var hasIcon = tab.Icon.Texture != null;
        tab.Holder.Visible = hasIcon;
        var label = tab.Label;
        label.AnchorLeft = 0;
        label.AnchorRight = 1;
        label.AnchorTop = hasIcon ? 1 : 0;
        label.AnchorBottom = 1;
        label.OffsetTop = hasIcon ? -48 : 0;
        label.OffsetBottom = hasIcon ? -4 : 0;
        label.AddThemeFontSizeOverride("font_size", hasIcon ? 34 : 46);
    }

    // ── what the game tells it ─────────────────────────────────────────────

    /// <summary>The dock is on screen (a run is loaded): its loops run.</summary>
    public void SetLive(bool live)
    {
        if (_live == live)
            return;
        _live = live;
        if (live)
            LoadAssets();
        RestartLoops();
    }

    public void ApplyFont(Font font)
    {
        if (font == null)
            return;
        foreach (var tab in _tabs.Values)
        {
            tab.Label.AddThemeFontOverride("font", font);
            tab.BadgeLabel?.AddThemeFontOverride("font", font);
            tab.BadgeOld?.AddThemeFontOverride("font", font);
        }
    }

    /// <summary>Takes the game's localized names, once its localization is up.</summary>
    public void RefreshTitles()
    {
        foreach (var (key, tab) in _tabs)
        {
            var (table, locKey, fallback) = TitleKeys[key];
            tab.Title = DsStyle.Loc(table, locKey, fallback);
            tab.Label.Text = tab.Title;
        }
    }

    public void SetIcon(string key, Texture2D texture)
    {
        if (texture == null || !_tabs.TryGetValue(key, out var tab) || tab.Icon.Texture == texture)
            return;
        tab.Icon.Texture = texture;
        Layout(tab);
        DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
    }

    /// <summary>Dimmed like a disabled top-bar button: a pile with nothing to show.</summary>
    public void SetEnabled(string key, bool enabled)
    {
        if (!_tabs.TryGetValue(key, out var tab) || tab.Enabled == enabled)
            return;
        tab.Enabled = enabled;
        ApplyState(tab, animate: true);
    }

    // How far a changing number rolls, and the game's pile button's
    // pressed colour (NCombatCardPile._downColor), for a count going down.
    private const float RollDistance = 8f;
    private static readonly Color DownColor = Colors.DarkGray;

    /// <summary>
    /// The pile's card count. It bumps when it grows, as the game's does,
    /// dips like the pressed button when it shrinks, and the number rolls
    /// between the two. It pops in when shown; negative shrinks it away.
    /// Changes in one frame (a hand dealt or discarded at once) make one
    /// tick, from the number shown to the newest.
    /// </summary>
    public void SetCount(string key, int count)
    {
        if (!_tabs.TryGetValue(key, out var tab) || tab.Badge == null || tab.Count == count)
            return;
        tab.Count = count;
        if (!_live)
        {
            tab.Shown = count;
            tab.Dirty = false;
            if (count >= 0)
                tab.BadgeLabel.Text = count.ToString();
            DsStyle.Kill(ref tab.Bump);
            DsStyle.Kill(ref tab.Tick);
            RestBadge(tab);
            tab.Badge.Visible = count >= 0;
            DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
            return;
        }
        tab.Dirty = true;
        if (_flushQueued)
            return;
        _flushQueued = true;
        _flushCounts.CallDeferred();
    }

    private void FlushCounts()
    {
        _flushQueued = false;
        foreach (var tab in _tabs.Values)
        {
            if (!tab.Dirty)
                continue;
            tab.Dirty = false;
            var previous = tab.Shown;
            var count = tab.Count;
            if (previous == count)
                continue;
            tab.Shown = count;
            if (count >= 0)
                tab.BadgeLabel.Text = count.ToString();
            if (!_live)
            {
                RestBadge(tab);
                tab.Badge.Visible = count >= 0;
                continue;
            }
            if (count < 0)
                HideBadge(tab);
            else if (previous < 0 || !tab.Badge.Visible)
                ShowBadge(tab);
            else
                StartTick(tab, previous.ToString(), grew: count > previous);
        }
    }

    private static void RestBadge(Tab tab)
    {
        tab.Badge.Scale = Vector2.One;
        tab.Badge.Modulate = Colors.White;
        tab.Rocker.Scale = Vector2.One;
        tab.BadgeLabel.Position = Vector2.Zero;
        tab.BadgeLabel.Modulate = Colors.White;
        tab.BadgeOld.Visible = false;
    }

    // One count change, in one tween stepping one made-once callable. The
    // old number leaves as the new one arrives: upward when the count grows
    // (the new one flashing gold), downward when it shrinks. Growing, icon
    // and count jump to hover scale and ease back (NCombatCardPile.AddCard);
    // shrinking, the badge takes the pile button's pressed look instead,
    // smaller and darker, easing back. The game counts one card at a time,
    // so a burst over several frames reads as a counter ticking.
    private const float TickLength = 0.55f;

    private static void StartTick(Tab tab, string from, bool grew)
    {
        DsStyle.Kill(ref tab.Bump);
        DsStyle.Kill(ref tab.Tick);
        tab.Grew = grew;
        tab.BadgeOld.Text = from;
        tab.BadgeOld.Visible = true;
        StepTick(tab, 0f);
        tab.Tick = DsStyle.Animate(tab.Badge, TickLength);
        tab.Tick.TweenMethod(tab.TickStep, 0f, TickLength, TickLength);
    }

    private static void StepTick(Tab tab, float t)
    {
        var up = tab.Grew;
        var dy = up ? -RollDistance : RollDistance;
        // The roll: 0.2 s out and in; the arriving number fades in over
        // 0.15 s, and a gold one then cools to white over 0.4 s.
        var roll = DsEase.Apply(DsCurve.CubicOut, Math.Min(t / 0.2f, 1f));
        var old = tab.BadgeOld;
        if (old.Visible)
        {
            old.Position = new Vector2(0f, dy * roll);
            old.Modulate = Colors.White with { A = 1f - roll };
            if (t >= 0.2f)
                old.Visible = false;
        }
        var now = tab.BadgeLabel;
        now.Position = new Vector2(0f, -dy * (1f - roll));
        var arrive = up ? DsStyle.Gold : Colors.White;
        now.Modulate = t < 0.15f || !up
            ? arrive with { A = DsEase.Apply(DsCurve.CubicOut, Math.Min(t / 0.15f, 1f)) }
            : arrive.Lerp(Colors.White, DsEase.Apply(DsCurve.CubicOut, Math.Min((t - 0.15f) / 0.4f, 1f)));
        // The bump or dip: 0.5 s.
        var settle = DsEase.Apply(DsCurve.ExpoOut, Math.Min(t / 0.5f, 1f));
        if (up)
        {
            var scale = Vector2.One * (1.25f - 0.25f * settle);
            tab.Rocker.Scale = scale;
            tab.Badge.Scale = scale;
            tab.Badge.Modulate = Colors.White;
        }
        else
        {
            tab.Rocker.Scale = Vector2.One;
            tab.Badge.Scale = Vector2.One * (0.85f + 0.15f * settle);
            tab.Badge.Modulate = DownColor.Lerp(Colors.White, settle);
        }
    }

    private static void ShowBadge(Tab tab)
    {
        DsStyle.Kill(ref tab.Bump);
        DsStyle.Kill(ref tab.Tick);
        RestBadge(tab);
        tab.Badge.Visible = true;
        tab.Badge.Scale = Vector2.Zero;
        tab.Bump = DsStyle.Animate(tab.Badge, 0.3);
        tab.Bump.TweenProperty(tab.Badge, DsNodePaths.Scale, Vector2.One, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
    }

    private static void HideBadge(Tab tab)
    {
        DsStyle.Kill(ref tab.Tick);
        if (!tab.Badge.Visible)
            return;
        DsStyle.Kill(ref tab.Bump);
        tab.BadgeOld.Visible = false;
        tab.Bump = DsStyle.Animate(tab.Badge, 0.2).SetParallel(true);
        tab.Bump.TweenProperty(tab.Badge, DsNodePaths.Scale, Vector2.One * 0.6f, 0.2).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
        tab.Bump.TweenProperty(tab.Badge, DsNodePaths.ModulateA, 0f, 0.2).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Cubic);
        tab.Bump.Chain().TweenCallback(tab.BadgeHidden);
    }

    private static void OnBadgeHidden(Tab tab)
    {
        if (tab.Count >= 0)
            return;
        tab.Badge.Visible = false;
        RestBadge(tab);
    }

    /// <summary>The map is open, so a room can be chosen: the map icon rocks until it closes.</summary>
    public void SetTraveling(bool traveling)
    {
        if (_traveling == traveling)
            return;
        _traveling = traveling;
        RestartLoops();
    }

    // ── selection ──────────────────────────────────────────────────────────

    public void Select(string key, bool animate)
    {
        _selected = key;
        foreach (var tab in _tabs.Values)
            ApplyState(tab, animate);
        PlaceMarker(animate);
        RestartLoops();
    }

    // NSettingsTab and NCardPoolFilter: selected is cream, full colour and a
    // little larger; the rest half-cream, desaturated and a little smaller.
    private void ApplyState(Tab tab, bool animate)
    {
        var selected = tab.Key == _selected;
        var scale = Vector2.One * (selected ? SelectedScale : UnselectedScale);
        var color = selected ? DsStyle.Cream : DsStyle.HalfCream;
        var s = selected ? 1f : 0.3f;
        var v = selected ? 1f : 0.55f;
        var modulate = tab.Enabled ? Colors.White : DsStyle.Disabled;
        // Without the shader, brightness alone.
        var iconModulate = tab.Hsv != null ? Colors.White : new Color(v, v, v);
        DsStyle.Kill(ref tab.State);
        if (!animate)
        {
            tab.Holder.Scale = scale;
            tab.Label.Modulate = color;
            tab.Button.Modulate = modulate;
            tab.Icon.Modulate = iconModulate;
            tab.Hsv?.SetShaderParameter("s", s);
            tab.Hsv?.SetShaderParameter("v", v);
            DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
            return;
        }
        tab.State = DsStyle.Animate(tab.Holder, 0.5).SetParallel(true);
        if (selected)
            tab.State.TweenProperty(tab.Holder, DsNodePaths.Scale, scale, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        else
            tab.State.TweenProperty(tab.Holder, DsNodePaths.Scale, scale, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tab.State.TweenProperty(tab.Label, DsNodePaths.Modulate, color, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tab.State.TweenProperty(tab.Button, DsNodePaths.Modulate, modulate, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tab.State.TweenProperty(tab.Icon, DsNodePaths.Modulate, iconModulate, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        if (tab.Hsv != null)
        {
            tab.State.TweenProperty(tab.Hsv, DsNodePaths.ShaderS, s, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            tab.State.TweenProperty(tab.Hsv, DsNodePaths.ShaderV, v, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
    }

    // The top-bar buttons: pressed, the icon tips and darkens; let go, it
    // swings back past upright and settles elastically.
    private void OnDown(Tab tab)
    {
        DsStyle.Kill(ref tab.Press);
        tab.Press = DsStyle.Animate(tab.Holder, 0.25).SetParallel(true);
        tab.Press.TweenProperty(tab.Holder, DsNodePaths.Rotation, Mathf.Pi / 15f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tab.Press.TweenProperty(tab.Label, DsNodePaths.Modulate, DsStyle.Gold, 0.05);
        if (tab.Hsv != null)
            tab.Press.TweenProperty(tab.Hsv, DsNodePaths.ShaderV, 0.4f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
    }

    private void OnUp(Tab tab)
    {
        DsStyle.Kill(ref tab.Press);
        tab.Press = DsStyle.Animate(tab.Holder, 1.25);
        tab.Press.TweenProperty(tab.Holder, DsNodePaths.Rotation, -Mathf.Pi / 15f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        tab.Press.TweenProperty(tab.Holder, DsNodePaths.Rotation, 0f, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Elastic);
        // Back to its state's colour and brightness (Select may have just changed it).
        ApplyState(tab, animate: true);
    }

    private void PlaceMarker(bool animate)
    {
        if (_selected == null || !_tabs.TryGetValue(_selected, out var tab))
        {
            if (_marker.Visible)
            {
                _marker.Visible = false;
                DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
            }
            return;
        }
        var shown = _marker.Visible;
        _marker.Visible = true;
        var rect = tab.Button.GetGlobalRect();
        if (rect.Size.X <= 0)
        {
            // Not laid out yet.
            Callable.From(() => PlaceMarker(animate: false)).CallDeferred();
            return;
        }
        var origin = _root.GetGlobalRect().Position;
        var width = rect.Size.X * 0.56f;
        var bottom = _shape == DockShape.Strip ? _root.Size.Y : _root.Size.Y - CornerShadow;
        var position = new Vector2(rect.Position.X - origin.X + (rect.Size.X - width) / 2, bottom - 12);
        var size = new Vector2(width, 6);
        DsStyle.Kill(ref _markerMove);
        if (!animate || !shown || _marker.Size.X <= 0)
        {
            _marker.Position = position;
            _marker.Size = size;
            DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
            return;
        }
        _markerMove = DsStyle.Animate(_marker, 0.3).SetParallel(true);
        _markerMove.TweenProperty(_marker, DsNodePaths.Position, position, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        _markerMove.TweenProperty(_marker, DsNodePaths.Size, size, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
    }

    // ── forever ────────────────────────────────────────────────────────────

    private void RestartLoops()
    {
        if (!_live)
        {
            DsStyle.Kill(ref _markerBreath);
            foreach (var tab in _tabs.Values)
                StopRock(tab, animate: false);
            _marker.Modulate = Colors.White;
            return;
        }
        if (_selected == null || !_tabs.ContainsKey(_selected))
        {
            DsStyle.Kill(ref _markerBreath);
            _marker.Modulate = Colors.White;
        }
        else if (!DsStyle.Running(_markerBreath))
        {
            _markerBreath = DsStyle.Loop(_marker);
            _markerBreath.TweenProperty(_marker, DsNodePaths.ModulateA, 0.65f, 1.2).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            _markerBreath.TweenProperty(_marker, DsNodePaths.ModulateA, 1f, 1.2).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
        }
        foreach (var tab in _tabs.Values)
        {
            var rock = tab.Key == "map" ? _traveling : tab.Key == _selected;
            if (rock)
                StartRock(tab);
            else
                StopRock(tab, animate: true);
        }
    }

    private static void StartRock(Tab tab)
    {
        if (DsStyle.Running(tab.Rock))
            return;
        DsStyle.Kill(ref tab.Rock);
        var rocker = tab.Rocker;
        if (tab.Key == "map")
        {
            // NTopBarMapButton.StartOscillation.
            tab.Rock = DsStyle.Loop(rocker);
            tab.Rock.TweenProperty(rocker, DsNodePaths.Rotation, -0.12f, 0.8).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            tab.Rock.TweenProperty(rocker, DsNodePaths.Rotation, 0.12f, 0.8).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
        }
        else
        {
            // NTopBarDeckButton while its screen is open: 0.12 * sin(4t).
            tab.Rock = DsStyle.Loop(rocker);
            tab.Rock.TweenMethod(Callable.From<float>(t => rocker.Rotation = 0.12f * Mathf.Sin(t)), 0f, Mathf.Tau, Mathf.Tau / 4f);
        }
    }

    // NTopBarMapButton.StopOscillation.
    private static void StopRock(Tab tab, bool animate)
    {
        if (tab.Rock == null)
            return;
        DsStyle.Kill(ref tab.Rock);
        if (!animate)
        {
            tab.Rocker.Rotation = 0;
            return;
        }
        var settle = DsStyle.Animate(tab.Rocker, 0.5);
        settle.TweenProperty(tab.Rocker, DsNodePaths.Rotation, 0f, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Spring);
    }

    private static GradientTexture2D Gradient(Color top, Color bottom) =>
        new()
        {
            Gradient = new Gradient { Colors = new[] { top, bottom }, Offsets = new[] { 0f, 1f } },
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(0, 1),
            Width = 4,
            Height = 64,
        };
}

/// <summary>The strip and the corner plates, as one dock: one selection, and
/// the game's news for a tab goes to whichever holds it.</summary>
internal sealed class DsDockSet
{
    private readonly DsDock[] _docks;

    public DsDockSet(params DsDock[] docks) => _docks = docks;

    public bool Animating
    {
        get
        {
            foreach (var dock in _docks)
            {
                if (dock.Animating)
                    return true;
            }
            return false;
        }
    }

    private DsDock Holder(string key) => Array.Find(_docks, d => d.Has(key));

    public string Title(string key) => Holder(key)?.Title(key) ?? key;

    public Texture2D IconOf(string key) => Holder(key)?.IconOf(key);

    public void SetIcon(string key, Texture2D texture) => Holder(key)?.SetIcon(key, texture);

    public void SetEnabled(string key, bool enabled) => Holder(key)?.SetEnabled(key, enabled);

    public void SetCount(string key, int count) => Holder(key)?.SetCount(key, count);

    public void SetTraveling(bool traveling)
    {
        foreach (var dock in _docks)
            dock.SetTraveling(traveling);
    }

    public void Select(string key, bool animate)
    {
        foreach (var dock in _docks)
            dock.Select(key, animate);
    }

    public void SetLive(bool live)
    {
        foreach (var dock in _docks)
            dock.SetLive(live);
    }

    public void ApplyFont(Font font)
    {
        foreach (var dock in _docks)
            dock.ApplyFont(font);
    }

    public void RefreshTitles()
    {
        foreach (var dock in _docks)
            dock.RefreshTitles();
    }
}
