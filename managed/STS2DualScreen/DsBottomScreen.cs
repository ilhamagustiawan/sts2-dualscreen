using System;
using System.Collections.Generic;
using Godot;

namespace STS2DualScreen;

// The bottom screen.
//
// A SubViewport sized to the bottom panel holds a tab bar and one page per
// tab, docked along the bottom edge. Pages host the game's own nodes (its map
// screen, its card pile screens), moved or instantiated here by DsGameHooks, so
// they look and behave as they do on PC. The viewport renders only when a
// capture is due (see MarkChanged), is copied back asynchronously and handed to
// libsts2ds, which posts it to the Presentation on the other display unless it
// is unchanged; touches queued there come back as mouse events pushed into
// this viewport.
//
// Controller and keyboard input reaches only the root window. While the open
// map is the game's current screen, every non-pointer event the root receives
// is pushed in here too, so the map's focus, A to travel and the drawing
// cursor work as they would on one screen.
//
// Outside a run (boot, main menu) the screen is plain black and ignores
// touches; the tabs appear once a run is loaded.
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
    private const float TabBarHeight = 150f;

    private readonly DsConfig _config;
    private readonly SceneTree _tree;
    private readonly SubViewport _viewport;
    private readonly Control _root;
    private readonly Control _content;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, Label> _placeholders = new();
    private readonly CanvasLayer _hoverLayer;
    private readonly CanvasLayer _topHintLayer;
    private readonly Label _topHint;
    private bool _styled;
    private string _active;
    private int _generation = -1;
    private bool _surfaceReady;
    private bool _inRun;
    private ulong _nextFrameUsec;
    private ulong _lastStatsUsec;
    private double _readbackMsTotal;
    private int _readbackCount;
    private double _submitMsTotal;
    private int _submitCount;

    // Capture pacing. The viewport only renders when a capture is due: at
    // Config.Fps while the screen is changing (a touch, controller input on
    // the map, a tab switch, or the last capture differed), and at
    // IdleIntervalUsec otherwise, to notice changes nobody announced (a pile
    // updating, a map animation).
    private const ulong IdleIntervalUsec = 250_000;
    private const ulong ActiveWindowUsec = 1_000_000;
    private const int MaxInFlight = 3;
    private ulong _activeUntilUsec;
    private bool _readbackDue;

    // Async readback through the RenderingDevice, so the game thread never
    // waits on the GPU. Null on the Compatibility renderer: read back inline.
    private readonly RenderingDevice _rd;
    private readonly Callable _onFrameData;
    private int _inFlight;
    private ulong _lastRequestUsec;
    private int _requestWidth, _requestHeight;

    // Game frame pacing, for the stats line.
    private ulong _lastFrameUsec;
    private ulong _frameUsecTotal;
    private ulong _frameUsecMax;
    private int _frameCount;
    private int _hitches;
    private int _gcMinor;
    private int _gcMajor;

    // Touch → mouse
    private int _pointerId = -1;
    private Vector2 _pointerPos;
    private float _wheelAccumulator;
    private ulong _lastTouchUsec;
    private readonly Dictionary<int, Vector2> _fingers = new();
    private bool _twoFinger;
    private Vector2 _scrollMid;

    public bool Ready => _surfaceReady;

    /// <summary>A touch on the lower screen is down or ended in the last moment,
    /// so whatever it pressed was pressed by touch, not the controller.</summary>
    public bool TouchRecent => _pointerId >= 0 || Time.GetTicksUsec() - _lastTouchUsec < 300_000;
    public Vector2 CanvasSize => _viewport.Size2DOverride;

    /// <summary>Hover tips for nodes on this screen, above the pages and the dock.</summary>
    public CanvasLayer HoverLayer => _hoverLayer;

    public bool IsBottom(Node node) => node != null && GodotObject.IsInstanceValid(node) && node.GetViewport() == _viewport;

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

        // Shown alone outside a run.
        var blank = new ColorRect { Name = "Blank", Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        blank.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _viewport.AddChild(blank);

        _root = new Control { Name = "Root", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _viewport.AddChild(_root);

        var background = new ColorRect { Name = "Background", Color = new Color(0.07f, 0.06f, 0.05f) };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        background.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(background);

        _content = new Control { Name = "Content", ClipContents = true };
        _content.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _content.OffsetBottom = -TabBarHeight;
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
        SetPlaceholder("map", "The map shows here once a run starts.");
        SetPlaceholder("draw", "Your draw pile shows here during combat.");
        SetPlaceholder("discard", "Your discard pile shows here during combat.");
        SetPlaceholder("exhaust", "Exhausted cards show here during combat.");
        SetPlaceholder("deck", "Your deck shows here once a run starts.");

        BuildTabBar();

        _hoverLayer = new CanvasLayer { Name = "HoverTips", Layer = 100 };
        _viewport.AddChild(_hoverLayer);

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

        _rd = RenderingServer.GetRenderingDevice();
        _onFrameData = Callable.From<byte[]>(OnFrameData);
        DsLog.Info(_rd != null ? "bottom readback: async (RenderingDevice)" : "bottom readback: inline (no RenderingDevice)");

        _tree.Root.AddChild(_viewport);
        _tree.Root.AddChild(_topHintLayer);
        _tree.ProcessFrame += OnProcessFrame;
        _tree.Root.WindowInput += OnRootInput;
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
        MarkChanged();
    }

    /// <summary>Capture at full rate for a while: something on the bottom screen is moving.</summary>
    public void MarkChanged()
    {
        var now = Time.GetTicksUsec();
        if (now >= _activeUntilUsec)
            _nextFrameUsec = 0;
        _activeUntilUsec = now + ActiveWindowUsec;
    }

    public void Show(string tab)
    {
        if (!_pages.ContainsKey(tab))
            return;
        MarkChanged();
        _active = tab;
        foreach (var (key, page) in _pages)
            page.Visible = key == tab;
        foreach (var (key, button) in _buttons)
            button.ButtonPressed = key == tab;
        DsGameHooks.OnTabShown(tab);
    }

    public string ActiveTab => _active;

    public void SetTopHint(bool visible) => _topHintLayer.Visible = visible && _config.ShowTopHint && _surfaceReady && _inRun;

    /// <summary>The map took the controller: nothing on the top screen keeps focus.</summary>
    public void ReleaseTopFocus() => _tree.Root.GuiReleaseFocus();

    /// <summary>The map gave the controller back.</summary>
    public void ReleaseBottomFocus() => _viewport.GuiReleaseFocus();

    /// <summary>
    /// Restyles our text after a live game label: the font, colours, outline
    /// and shadow of the hint, and the font of the tabs and placeholders. Taken
    /// from the running game rather than a resource path so it follows the
    /// game's per-language font swap.
    /// </summary>
    public void ApplyGameTextStyle(Label reference)
    {
        if (_styled || reference == null || !GodotObject.IsInstanceValid(reference))
            return;
        _styled = true;
        var font = reference.GetThemeFont("font");
        if (reference.LabelSettings is { } settings)
        {
            var copy = (LabelSettings)settings.Duplicate();
            copy.FontSize = Math.Max(copy.FontSize, 36);
            _topHint.LabelSettings = copy;
            font = settings.Font ?? font;
        }
        else
        {
            _topHint.AddThemeFontOverride("font", font);
            _topHint.AddThemeColorOverride("font_color", reference.GetThemeColor("font_color"));
            _topHint.AddThemeColorOverride("font_outline_color", reference.GetThemeColor("font_outline_color"));
            _topHint.AddThemeConstantOverride("outline_size", reference.GetThemeConstant("outline_size"));
            _topHint.AddThemeColorOverride("font_shadow_color", reference.GetThemeColor("font_shadow_color"));
            _topHint.AddThemeConstantOverride("shadow_offset_x", reference.GetThemeConstant("shadow_offset_x"));
            _topHint.AddThemeConstantOverride("shadow_offset_y", reference.GetThemeConstant("shadow_offset_y"));
            _topHint.AddThemeConstantOverride("shadow_outline_size", reference.GetThemeConstant("shadow_outline_size"));
        }
        if (font == null)
            return;
        foreach (var button in _buttons.Values)
            button.AddThemeFontOverride("font", font);
        foreach (var label in _placeholders.Values)
            label.AddThemeFontOverride("font", font);
    }

    private void BuildTabBar()
    {
        var bar = new PanelContainer { Name = "TabBar" };
        bar.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        bar.OffsetTop = -TabBarHeight;
        var barStyle = new StyleBoxFlat { BgColor = new Color(0.12f, 0.1f, 0.08f) };
        barStyle.BorderWidthTop = 4;
        barStyle.BorderColor = new Color(0.55f, 0.42f, 0.24f);
        barStyle.ContentMarginLeft = barStyle.ContentMarginRight = 12;
        barStyle.ContentMarginTop = barStyle.ContentMarginBottom = 10;
        bar.AddThemeStyleboxOverride("panel", barStyle);
        _root.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
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
                ClipText = true,
                CustomMinimumSize = new Vector2(0, TabBarHeight - 24),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.Fill,
            };
            button.AddThemeFontSizeOverride("font_size", 54);
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
        style.SetCornerRadiusAll(14);
        style.ContentMarginLeft = style.ContentMarginRight = 12;
        return style;
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

    // ── controller ─────────────────────────────────────────────────────────

    // Emitted for every event the root window gets, before the root handles
    // it; that includes the ui_* actions the game makes from controller
    // buttons with Input.ParseInputEvent.
    private void OnRootInput(InputEvent e)
    {
        if (!_surfaceReady || !_inRun || e is InputEventMouse or InputEventScreenTouch or InputEventScreenDrag or InputEventGesture)
            return;
        try
        {
            if (DsGameHooks.MapOwnsInput())
            {
                _viewport.PushInput(e, false);
                MarkChanged();
                return;
            }
            if (e.IsPressed() && !e.IsEcho() && _tree.Root.GuiGetFocusOwner() == null && IsFocusAction(e))
                DsGameHooks.RestoreTopFocus($"nothing focused on {DsGameHooks.CurrentScreenName()}");
        }
        catch (Exception ex)
        {
            DsLog.Info($"controller forwarding failed: {ex.Message}");
        }
    }

    private static readonly string[] FocusActions = { "ui_accept", "ui_select", "ui_up", "ui_down", "ui_left", "ui_right" };

    private static bool IsFocusAction(InputEvent e)
    {
        foreach (var action in FocusActions)
        {
            if (e.IsAction(action))
                return true;
        }
        return false;
    }

    // ── per frame ──────────────────────────────────────────────────────────

    private void OnProcessFrame()
    {
        try
        {
            TrackFramePacing();
            var state = DsNative.State();
            if (!state.Ready)
            {
                if (_surfaceReady)
                {
                    _surfaceReady = false;
                    _readbackDue = false;
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
                _pointerId = -1;
                _fingers.Clear();
                _twoFinger = false;
                MarkChanged();
                DsLog.Info($"bottom surface {state.Width}x{state.Height}, canvas {_viewport.Size2DOverride}");
                DsGameHooks.OnSurfaceReady();
            }

            UpdateRunState();
            PumpTouches();

            var now = Time.GetTicksUsec();
            // Last frame drew the viewport; its pixels are ready to copy.
            if (_readbackDue)
            {
                _readbackDue = false;
                RequestReadback(now);
            }
            if (_inFlight > 0 && now - _lastRequestUsec > 2_000_000)
            {
                DsLog.Info($"bottom readback: {_inFlight} request(s) never answered; resetting");
                _inFlight = 0;
            }
            if (now >= _nextFrameUsec && _inFlight < MaxInFlight)
            {
                var interval = now < _activeUntilUsec ? (ulong)(1_000_000 / Math.Max(1, _config.Fps)) : IdleIntervalUsec;
                _nextFrameUsec = now + interval;
                RenderingServer.ViewportSetUpdateMode(_viewport.GetViewportRid(), RenderingServer.ViewportUpdateMode.Once);
                _readbackDue = true;
            }
            if (now - _lastStatsUsec > 10_000_000)
            {
                _lastStatsUsec = now;
                LogStats();
            }
        }
        catch (Exception e)
        {
            DsLog.Info($"frame failed, bottom screen stopped: {e}");
            _tree.ProcessFrame -= OnProcessFrame;
        }
    }

    private void UpdateRunState()
    {
        var inRun = DsGameHooks.InRun();
        if (inRun == _inRun)
            return;
        _inRun = inRun;
        _root.Visible = inRun;
        ReleasePointer();
        MarkChanged();
        DsLog.Info(inRun ? "run started; lower screen shown" : "no run; lower screen blank");
        if (!inRun)
        {
            _topHintLayer.Visible = false;
            return;
        }
        Show(Array.IndexOf(Tabs, _config.DefaultTab) >= 0 ? _config.DefaultTab : "map");
        DsGameHooks.OnSurfaceReady();
    }

    private void ReleasePointer()
    {
        if (_pointerId >= 0)
            Push(MouseButtonEvent(_pointerPos, pressed: false));
        _pointerId = -1;
        _fingers.Clear();
        _twoFinger = false;
    }

    // A hitch is a game frame that took more than 25 ms (a dropped frame at 60 fps).
    private void TrackFramePacing()
    {
        var now = Time.GetTicksUsec();
        if (_lastFrameUsec != 0)
        {
            var dt = now - _lastFrameUsec;
            _frameUsecTotal += dt;
            _frameUsecMax = Math.Max(_frameUsecMax, dt);
            _frameCount++;
            if (dt > 25_000)
                _hitches++;
        }
        _lastFrameUsec = now;
    }

    private void LogStats()
    {
        var stats = DsNative.Stats();
        var readback = _readbackCount > 0 ? _readbackMsTotal / _readbackCount : 0;
        var submit = _submitCount > 0 ? _submitMsTotal / _submitCount : 0;
        var frame = _frameCount > 0 ? _frameUsecTotal / 1000.0 / _frameCount : 0;
        var gcMinor = GC.CollectionCount(0);
        var gcMajor = GC.CollectionCount(GC.MaxGeneration);
        DsLog.Info($"frames posted={stats.Posted} unchanged={stats.Skipped} touch_overflows={stats.Overflows} readback_avg={readback:F1}ms submit_avg={submit:F1}ms captures={_readbackCount} tab={_active}"
            + $" | game frame_avg={frame:F1}ms frame_max={_frameUsecMax / 1000.0:F1}ms hitches={_hitches}/{_frameCount} gc_minor=+{gcMinor - _gcMinor} gc_major=+{gcMajor - _gcMajor}");
        _readbackMsTotal = _submitMsTotal = 0;
        _readbackCount = _submitCount = 0;
        _frameUsecTotal = _frameUsecMax = 0;
        _frameCount = _hitches = 0;
        _gcMinor = gcMinor;
        _gcMajor = gcMajor;
    }

    // Queues a GPU copy of what the viewport drew last frame. The copy lands in
    // OnFrameData a couple of frames later, once the GPU is done with it.
    private void RequestReadback(ulong now)
    {
        if (_rd == null)
        {
            ReadbackInline();
            return;
        }
        var texture = RenderingServer.TextureGetRdTexture(_viewport.GetTexture().GetRid());
        if (!texture.IsValid)
            return;
        var size = _viewport.Size;
        _requestWidth = size.X;
        _requestHeight = size.Y;
        if (_rd.TextureGetDataAsync(texture, 0, _onFrameData) != Error.Ok)
            return;
        _inFlight++;
        _lastRequestUsec = now;
        _readbackMsTotal += (Time.GetTicksUsec() - now) / 1000.0;
        _readbackCount++;
    }

    private void OnFrameData(byte[] data)
    {
        if (_inFlight > 0)
            _inFlight--;
        if (!_surfaceReady)
            return;
        var width = _requestWidth;
        var height = _requestHeight;
        if (data == null || data.Length != width * height * 4)
            return; // resized while in flight
        Submit(data, width, height);
    }

    private void ReadbackInline()
    {
        var started = Time.GetTicksUsec();
        using var image = _viewport.GetTexture()?.GetImage();
        if (image == null || image.IsEmpty())
            return;
        if (image.GetFormat() != Image.Format.Rgba8)
            image.Convert(Image.Format.Rgba8);
        var data = image.GetData();
        _readbackMsTotal += (Time.GetTicksUsec() - started) / 1000.0;
        _readbackCount++;
        Submit(data, image.GetWidth(), image.GetHeight());
    }

    private unsafe void Submit(byte[] data, int width, int height)
    {
        var started = Time.GetTicksUsec();
        int posted;
        fixed (byte* pixels = data)
        {
            posted = DsNative.SubmitFrame(pixels, width, height, width * 4);
        }
        _submitMsTotal += (Time.GetTicksUsec() - started) / 1000.0;
        _submitCount++;
        // It differed from the last frame: the screen is moving, keep up with it.
        if (posted == 1)
            MarkChanged();
    }

    private unsafe void PumpTouches()
    {
        const int Max = 64;
        var touches = stackalloc DsNative.Touch[Max];
        var count = DsNative.PollTouches(touches, Max);
        // Nothing to touch outside a run; drop them.
        if (!_inRun)
            return;
        if (count > 0)
            MarkChanged();
        for (var i = 0; i < count; i++)
        {
            var t = touches[i];
            // Canvas coordinates, pushed as local: PushInput would map
            // Position through the stretch but leave GlobalPosition in surface
            // pixels, and the map draws lines at GlobalPosition.
            var pos = new Vector2(t.X, t.Y) * CanvasSize;
            _lastTouchUsec = Time.GetTicksUsec();
            switch (t.Phase)
            {
                case DsNative.PhaseDown:
                    _fingers[t.Id] = pos;
                    if (_fingers.Count == 1 && !_twoFinger)
                    {
                        _pointerId = t.Id;
                        _pointerPos = pos;
                        _wheelAccumulator = 0;
                        Push(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                        Push(MouseButtonEvent(pos, pressed: true));
                    }
                    else if (_fingers.Count == 2)
                    {
                        // A second finger turns the gesture into a scroll: let go
                        // of the first finger's press (ending a drawn line or a
                        // drag) and follow the midpoint from here.
                        if (_pointerId >= 0)
                            Push(MouseButtonEvent(_pointerPos, pressed: false));
                        _pointerId = -1;
                        _twoFinger = true;
                        _wheelAccumulator = 0;
                        _scrollMid = Midpoint();
                    }
                    break;
                case DsNative.PhaseMove:
                    if (!_fingers.ContainsKey(t.Id))
                        break;
                    _fingers[t.Id] = pos;
                    if (_twoFinger)
                    {
                        if (_fingers.Count >= 2)
                            TwoFingerScroll();
                        break;
                    }
                    if (t.Id != _pointerId)
                        break;
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
                case DsNative.PhaseUp:
                case DsNative.PhaseCancel:
                    _fingers.Remove(t.Id);
                    if (t.Id == _pointerId)
                    {
                        Push(MouseButtonEvent(pos, pressed: false));
                        _pointerId = -1;
                    }
                    // The rest of a two-finger gesture never becomes a press.
                    if (_fingers.Count == 0)
                        _twoFinger = false;
                    else if (_twoFinger)
                        _scrollMid = Midpoint();
                    break;
            }
        }
    }

    private Vector2 Midpoint()
    {
        var sum = Vector2.Zero;
        var n = 0;
        foreach (var p in _fingers.Values)
        {
            sum += p;
            if (++n == 2)
                break;
        }
        return n == 0 ? Vector2.Zero : sum / n;
    }

    // The map scrolls on trackpad pan gestures (drag = -Delta.Y * 50) and the
    // card grids on the wheel, so a two-finger drag becomes one or the other.
    private void TwoFingerScroll()
    {
        var mid = Midpoint();
        var dy = mid.Y - _scrollMid.Y;
        _scrollMid = mid;
        if (dy == 0f)
            return;
        if (_active == "map")
            Push(new InputEventPanGesture { Position = mid, Delta = new Vector2(0f, -dy / 50f) });
        else
            EmulateWheel(mid, dy);
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

    private void Push(InputEvent e) => _viewport.PushInput(e, true);
}
