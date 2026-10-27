using System;
using System.Collections.Generic;
using Godot;

namespace STS2DualScreen;

// The bottom screen.
//
// A SubViewport sized to the bottom panel holds a tab bar and one page per
// tab. Pages host the game's own nodes (its map screen, its card pile screens),
// moved or instantiated here by DsGameHooks, so they look and behave as they
// do on PC. Every frame (capped at Config.Fps) the viewport is read back and
// handed to libsts2ds, which posts it to the Presentation on the other display;
// touches queued there come back as mouse events pushed into this viewport.
//
// Nothing here subclasses a Godot type: this assembly is built without the
// Godot source generators, so virtual callbacks like _Process would never
// fire. Everything runs off SceneTree.ProcessFrame and signals instead.
internal sealed class DsBottomScreen
{
    public static DsBottomScreen Instance { get; private set; }

    public static readonly string[] Tabs = { "map", "draw", "discard", "exhaust", "deck" };
    private static readonly Dictionary<string, string> TabTitles = new()
    {
        ["map"] = "Map",
        ["draw"] = "Draw",
        ["discard"] = "Discard",
        ["exhaust"] = "Exhaust",
        ["deck"] = "Deck",
    };
    private const float TabBarHeight = 96f;

    private readonly DsConfig _config;
    private readonly SceneTree _tree;
    private readonly SubViewport _viewport;
    private readonly Control _root;
    private readonly Control _content;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, Label> _placeholders = new();
    private readonly CanvasLayer _topHintLayer;
    private readonly Label _topHint;
    private string _active;
    private int _generation = -1;
    private bool _surfaceReady;
    private ulong _nextFrameUsec;
    private ulong _lastStatsUsec;
    private double _readbackMsTotal;
    private int _readbackCount;

    // Touch → mouse
    private int _pointerId = -1;
    private Vector2 _pointerPos;
    private float _wheelAccumulator;

    public bool Ready => _surfaceReady;
    public Vector2 CanvasSize => _viewport.Size2DOverride;

    private DsBottomScreen(SceneTree tree, DsConfig config)
    {
        _tree = tree;
        _config = config;

        _viewport = new SubViewport
        {
            Name = "DsBottomScreen",
            Disable3D = true,
            TransparentBg = false,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Size2DOverrideStretch = true,
        };
        ApplySize(1240, 1080);

        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _viewport.AddChild(_root);

        var background = new ColorRect { Name = "Background", Color = new Color(0.07f, 0.06f, 0.05f) };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        background.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(background);

        _content = new Control { Name = "Content", ClipContents = true };
        _content.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _content.OffsetTop = TabBarHeight;
        _content.MouseFilter = Control.MouseFilterEnum.Pass;
        _root.AddChild(_content);

        foreach (var tab in Tabs)
        {
            var page = new Control { Name = $"Page_{tab}", Visible = false, MouseFilter = Control.MouseFilterEnum.Pass };
            page.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _content.AddChild(page);
            _pages[tab] = page;

            var placeholder = new Label
            {
                Name = "Placeholder",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            placeholder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            placeholder.AddThemeFontSizeOverride("font_size", 40);
            placeholder.AddThemeColorOverride("font_color", new Color(0.85f, 0.8f, 0.7f));
            page.AddChild(placeholder);
            _placeholders[tab] = placeholder;
        }
        SetPlaceholder("map", "The map opens here.\n\nTap to view it.");
        SetPlaceholder("draw", "Your draw pile shows here during combat.");
        SetPlaceholder("discard", "Your discard pile shows here during combat.");
        SetPlaceholder("exhaust", "Exhausted cards show here during combat.");
        SetPlaceholder("deck", "Your deck shows here once a run starts.");
        _placeholders["map"].MouseFilter = Control.MouseFilterEnum.Stop;
        _placeholders["map"].GuiInput += OnMapPlaceholderInput;

        BuildTabBar();

        _topHintLayer = new CanvasLayer { Name = "DsTopHint", Layer = 120, Visible = false };
        _topHint = new Label
        {
            Text = "Choose your path on the lower screen",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _topHint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _topHint.OffsetBottom = -48;
        _topHint.AddThemeFontSizeOverride("font_size", 36);
        _topHint.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.7f));
        _topHint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _topHint.AddThemeConstantOverride("outline_size", 10);
        _topHintLayer.AddChild(_topHint);

        _tree.Root.AddChild(_viewport);
        _tree.Root.AddChild(_topHintLayer);
        _tree.ProcessFrame += OnProcessFrame;
        Show(Array.IndexOf(Tabs, config.DefaultTab) >= 0 ? config.DefaultTab : "map");
    }

    public static void Create(SceneTree tree, DsConfig config)
    {
        if (Instance != null)
            return;
        Instance = new DsBottomScreen(tree, config);
        DsLog.Info("bottom screen host created");
        DsGameHooks.OnBottomScreenReady(Instance);
    }

    public Control Page(string tab) => _pages[tab];

    public void SetPlaceholder(string tab, string text)
    {
        var label = _placeholders[tab];
        label.Text = text;
        label.Visible = !string.IsNullOrEmpty(text);
    }

    public void Show(string tab)
    {
        if (!_pages.ContainsKey(tab))
            return;
        _active = tab;
        foreach (var (key, page) in _pages)
            page.Visible = key == tab;
        foreach (var (key, button) in _buttons)
            button.ButtonPressed = key == tab;
        DsGameHooks.OnTabShown(tab);
    }

    public string ActiveTab => _active;

    public void SetTopHint(bool visible) => _topHintLayer.Visible = visible && _config.ShowTopHint && _surfaceReady;

    private void BuildTabBar()
    {
        var bar = new PanelContainer { Name = "TabBar" };
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetBottom = TabBarHeight;
        var barStyle = new StyleBoxFlat { BgColor = new Color(0.12f, 0.1f, 0.08f) };
        barStyle.BorderWidthBottom = 3;
        barStyle.BorderColor = new Color(0.55f, 0.42f, 0.24f);
        bar.AddThemeStyleboxOverride("panel", barStyle);
        _root.AddChild(bar);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 8);
        bar.AddChild(row);

        var group = new ButtonGroup();
        foreach (var tab in Tabs)
        {
            var button = new Button
            {
                Text = TabTitles[tab],
                ToggleMode = true,
                ButtonGroup = group,
                FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(220, TabBarHeight - 16),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            button.AddThemeFontSizeOverride("font_size", 38);
            button.AddThemeStyleboxOverride("normal", TabStyle(new Color(0.18f, 0.15f, 0.12f)));
            button.AddThemeStyleboxOverride("hover", TabStyle(new Color(0.24f, 0.2f, 0.15f)));
            button.AddThemeStyleboxOverride("pressed", TabStyle(new Color(0.45f, 0.33f, 0.17f)));
            button.AddThemeStyleboxOverride("hover_pressed", TabStyle(new Color(0.5f, 0.37f, 0.2f)));
            button.AddThemeColorOverride("font_color", new Color(0.8f, 0.75f, 0.65f));
            button.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.95f, 0.8f));
            var key = tab;
            button.Pressed += () => Show(key);
            row.AddChild(button);
            _buttons[tab] = button;
        }
    }

    private static StyleBoxFlat TabStyle(Color color)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetCornerRadiusAll(10);
        style.ContentMarginLeft = style.ContentMarginRight = 12;
        return style;
    }

    private void OnMapPlaceholderInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            DsGameHooks.OpenMapFromBottom();
    }

    private void ApplySize(int width, int height)
    {
        _viewport.Size = new Vector2I(width, height);
        var scale = _config.CanvasScale;
        _viewport.Size2DOverride = new Vector2I(
            (int)MathF.Round(width * scale),
            (int)MathF.Round(height * scale)
        );
    }

    // ── per frame ──────────────────────────────────────────────────────────

    private void OnProcessFrame()
    {
        try
        {
            var state = DsNative.State();
            if (!state.Ready)
            {
                if (_surfaceReady)
                {
                    _surfaceReady = false;
                    _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                    _topHintLayer.Visible = false;
                    DsLog.Info("bottom surface gone; paused");
                }
                return;
            }
            if (!_surfaceReady || state.Generation != _generation)
            {
                _surfaceReady = true;
                _generation = state.Generation;
                ApplySize(state.Width, state.Height);
                _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                _pointerId = -1;
                DsLog.Info($"bottom surface {state.Width}x{state.Height}, canvas {_viewport.Size2DOverride}");
                DsGameHooks.OnSurfaceReady();
            }

            PumpTouches(state.Width, state.Height);

            var now = Time.GetTicksUsec();
            if (now >= _nextFrameUsec)
            {
                _nextFrameUsec = now + (ulong)(1_000_000 / Math.Max(1, _config.Fps));
                SubmitFrame();
            }
            if (now - _lastStatsUsec > 10_000_000)
            {
                _lastStatsUsec = now;
                var stats = DsNative.Stats();
                var avg = _readbackCount > 0 ? _readbackMsTotal / _readbackCount : 0;
                DsLog.Info($"frames posted={stats.Posted} unchanged={stats.Skipped} touch_overflows={stats.Overflows} readback_avg={avg:F1}ms tab={_active}");
                _readbackMsTotal = 0;
                _readbackCount = 0;
            }
        }
        catch (Exception e)
        {
            DsLog.Info($"frame failed, bottom screen stopped: {e}");
            _tree.ProcessFrame -= OnProcessFrame;
        }
    }

    private unsafe void SubmitFrame()
    {
        var started = Time.GetTicksUsec();
        using var image = _viewport.GetTexture()?.GetImage();
        if (image == null || image.IsEmpty())
            return;
        if (image.GetFormat() != Image.Format.Rgba8)
            image.Convert(Image.Format.Rgba8);
        var width = image.GetWidth();
        var height = image.GetHeight();
        var data = image.GetData();
        _readbackMsTotal += (Time.GetTicksUsec() - started) / 1000.0;
        _readbackCount++;
        fixed (byte* pixels = data)
        {
            DsNative.SubmitFrame(pixels, width, height, width * 4);
        }
    }

    private unsafe void PumpTouches(int width, int height)
    {
        const int Max = 64;
        var touches = stackalloc DsNative.Touch[Max];
        var count = DsNative.PollTouches(touches, Max);
        for (var i = 0; i < count; i++)
        {
            var t = touches[i];
            // Physical surface pixels; PushInput maps them through the
            // viewport's stretch transform onto the canvas.
            var pos = new Vector2(t.X * width, t.Y * height);
            switch (t.Phase)
            {
                case DsNative.PhaseDown when _pointerId < 0:
                    _pointerId = t.Id;
                    _pointerPos = pos;
                    _wheelAccumulator = 0;
                    Push(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                    Push(MouseButtonEvent(pos, pressed: true));
                    break;
                case DsNative.PhaseMove when t.Id == _pointerId:
                    var relative = pos - _pointerPos;
                    _pointerPos = pos;
                    Push(new InputEventMouseMotion
                    {
                        Position = pos,
                        GlobalPosition = pos,
                        Relative = relative,
                        ButtonMask = MouseButtonMask.Left,
                    });
                    if (_active != "map")
                        EmulateWheel(pos, relative.Y);
                    break;
                case DsNative.PhaseUp when t.Id == _pointerId:
                case DsNative.PhaseCancel when t.Id == _pointerId:
                    Push(MouseButtonEvent(pos, pressed: false));
                    _pointerId = -1;
                    break;
            }
        }
    }

    // Card grids scroll with the wheel on PC; turn a vertical drag into wheel steps.
    private void EmulateWheel(Vector2 pos, float dy)
    {
        _wheelAccumulator += dy;
        const float Step = 40f;
        while (MathF.Abs(_wheelAccumulator) >= Step)
        {
            var up = _wheelAccumulator > 0;
            _wheelAccumulator -= up ? Step : -Step;
            var button = up ? MouseButton.WheelUp : MouseButton.WheelDown;
            Push(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = pos, GlobalPosition = pos, Factor = 1f });
            Push(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = pos, GlobalPosition = pos, Factor = 1f });
        }
    }

    private static InputEventMouseButton MouseButtonEvent(Vector2 pos, bool pressed) =>
        new()
        {
            ButtonIndex = MouseButton.Left,
            Pressed = pressed,
            Position = pos,
            GlobalPosition = pos,
            ButtonMask = pressed ? MouseButtonMask.Left : 0,
        };

    private void Push(InputEvent e) => _viewport.PushInput(e, false);
}
