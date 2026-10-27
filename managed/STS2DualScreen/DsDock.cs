using System;
using System.Collections.Generic;
using Godot;

namespace STS2DualScreen;

// The tab dock along the bottom edge, drawn in its own small viewport so that
// its motion never costs a full-screen capture.
//
// Each tab is the game's icon over its localized name, styled and moving as
// the game's own buttons do: the settings tabs' selected/unselected look
// (cream or half-cream text, the hsv shader's s and v), the top-bar buttons'
// press and elastic settle, the combat piles' count bump, and, forever while
// it applies, the top-bar map button's rocking when travel is possible and
// the deck button's rocking while its screen is open. A gold marker slides to
// the selected tab and breathes.
internal sealed class DsDock
{
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
        public string Title;
        public int Count = -1;
        public bool Enabled = true;
        public Tween State, Press, Bump, Rock;
    }

    private readonly Control _root;
    private readonly Action<string> _onPressed;
    private readonly Dictionary<string, Tab> _tabs = new();
    private readonly Panel _marker;
    private Tween _markerMove, _markerBreath;
    private string _selected;
    private bool _traveling;
    private bool _live;

    public DsDock(Control root, string[] tabs, Action<string> onPressed)
    {
        _root = root;
        _onPressed = onPressed;

        // Opaque: it is posted on its own, not over the pages. Darker at the
        // bottom edge, with a faint gold rim and glow along the top like the
        // game's framed panels.
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

        var row = new HBoxContainer { Name = "Tabs", MouseFilter = Control.MouseFilterEnum.Pass };
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 12;
        row.OffsetRight = -12;
        row.OffsetTop = 4;
        row.AddThemeConstantOverride("separation", 8);
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
        _marker = new Panel { Name = "Marker", MouseFilter = Control.MouseFilterEnum.Ignore };
        _marker.AddThemeStyleboxOverride("panel", markerStyle);
        _marker.Size = new Vector2(0, 6);
        _root.AddChild(_marker);
        _root.Resized += () => PlaceMarker(animate: false);
    }

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
            if (DsStyle.Running(_markerBreath))
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
            var count = DsStyle.Text(27, 6);
            count.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            badge.AddChild(count);
            button.AddChild(badge);
            tab.Badge = badge;
            tab.BadgeLabel = count;
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

    /// <summary>The pile's card count; it bumps when it grows, as the game's does. Negative hides it.</summary>
    public void SetCount(string key, int count)
    {
        if (!_tabs.TryGetValue(key, out var tab) || tab.Badge == null || tab.Count == count)
            return;
        var grew = tab.Count >= 0 && count > tab.Count;
        tab.Count = count;
        tab.Badge.Visible = count >= 0;
        if (count >= 0)
            tab.BadgeLabel.Text = count.ToString();
        if (!grew)
        {
            DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
            return;
        }
        DsStyle.Kill(ref tab.Bump);
        tab.Bump = DsStyle.Animate(tab.Rocker, 0.5).SetParallel(true);
        tab.Rocker.Scale = Vector2.One * 1.25f;
        tab.Badge.Scale = Vector2.One * 1.25f;
        tab.Bump.TweenProperty(tab.Rocker, "scale", Vector2.One, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tab.Bump.TweenProperty(tab.Badge, "scale", Vector2.One, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
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
            tab.State.TweenProperty(tab.Holder, "scale", scale, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        else
            tab.State.TweenProperty(tab.Holder, "scale", scale, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tab.State.TweenProperty(tab.Label, "modulate", color, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tab.State.TweenProperty(tab.Button, "modulate", modulate, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tab.State.TweenProperty(tab.Icon, "modulate", iconModulate, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        if (tab.Hsv != null)
        {
            tab.State.TweenProperty(tab.Hsv, "shader_parameter/s", s, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            tab.State.TweenProperty(tab.Hsv, "shader_parameter/v", v, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
    }

    // The top-bar buttons: pressed, the icon tips and darkens; let go, it
    // swings back past upright and settles elastically.
    private void OnDown(Tab tab)
    {
        DsStyle.Kill(ref tab.Press);
        tab.Press = DsStyle.Animate(tab.Holder, 0.25).SetParallel(true);
        tab.Press.TweenProperty(tab.Holder, "rotation", Mathf.Pi / 15f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tab.Press.TweenProperty(tab.Label, "modulate", DsStyle.Gold, 0.05);
        if (tab.Hsv != null)
            tab.Press.TweenProperty(tab.Hsv, "shader_parameter/v", 0.4f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
    }

    private void OnUp(Tab tab)
    {
        DsStyle.Kill(ref tab.Press);
        tab.Press = DsStyle.Animate(tab.Holder, 1.25);
        tab.Press.TweenProperty(tab.Holder, "rotation", -Mathf.Pi / 15f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        tab.Press.TweenProperty(tab.Holder, "rotation", 0f, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Elastic);
        // Back to its state's colour and brightness (Select may have just changed it).
        ApplyState(tab, animate: true);
    }

    private void PlaceMarker(bool animate)
    {
        if (_selected == null || !_tabs.TryGetValue(_selected, out var tab))
            return;
        var rect = tab.Button.GetGlobalRect();
        if (rect.Size.X <= 0)
        {
            // Not laid out yet.
            Callable.From(() => PlaceMarker(animate: false)).CallDeferred();
            return;
        }
        var origin = _root.GetGlobalRect().Position;
        var width = rect.Size.X * 0.56f;
        var position = new Vector2(rect.Position.X - origin.X + (rect.Size.X - width) / 2, _root.Size.Y - 12);
        var size = new Vector2(width, 6);
        DsStyle.Kill(ref _markerMove);
        if (!animate || _marker.Size.X <= 0)
        {
            _marker.Position = position;
            _marker.Size = size;
            DsBottomScreen.Instance?.MarkChanged(_root, 0.1);
            return;
        }
        _markerMove = DsStyle.Animate(_marker, 0.3).SetParallel(true);
        _markerMove.TweenProperty(_marker, "position", position, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        _markerMove.TweenProperty(_marker, "size", size, 0.3).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
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
        if (!DsStyle.Running(_markerBreath))
        {
            _markerBreath = DsStyle.Loop(_marker);
            _markerBreath.TweenProperty(_marker, "modulate:a", 0.65f, 1.2).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            _markerBreath.TweenProperty(_marker, "modulate:a", 1f, 1.2).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
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
            tab.Rock.TweenProperty(rocker, "rotation", -0.12f, 0.8).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            tab.Rock.TweenProperty(rocker, "rotation", 0.12f, 0.8).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
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
        settle.TweenProperty(tab.Rocker, "rotation", 0f, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Spring);
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
