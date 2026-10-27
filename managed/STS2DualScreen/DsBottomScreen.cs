using System;
using System.Collections.Generic;
using Godot;
using Godot.NativeInterop;

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
// Outside a run (boot, main menu) the screen shows the game's own animated
// main-menu art and ignores touches; the tabs appear once a run is loaded.
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

    // The main menu's backdrop scene (Spine art, particles, the logo), drawn
    // for 1920x1080 and scaled to cover the canvas. Loaded on a thread while
    // outside a run and freed when one starts, so its textures don't stay
    // resident during play. It plays briefly to reach its pose, then is
    // frozen into a still and the screen stops capturing: animated, it cost
    // the menu a dropped frame in four.
    private const string IdleArtScene = "res://scenes/backgrounds/main_menu_bg.tscn";
    private static readonly Vector2 IdleArtSize = new(1920, 1080);
    private const int IdleArtFps = 24;
    private const ulong IdleArtSettleUsec = 1_500_000;
    private ulong _idleArtShownUsec;
    private bool _idleArtFrozen;
    private const int PileFps = 30;
    // In a fight every card move redraws a pile grid while the top screen is
    // busiest; half the rate there is still smooth enough for cards moving.
    private const int CombatFps = 15;
    private Control _idle;
    private Control _idleArt;
    private bool _idleArtLoading;
    private bool _idleArtFailed;
    private ulong _nextFrameUsec;
    private ulong _lastStatsUsec;
    private double _readbackMsTotal;
    private int _readbackCount;
    private double _submitMsTotal;
    private int _submitCount;

    // Capture pacing. The viewport only renders when a capture is due: at
    // Config.Fps while the screen is changing (a touch, controller input on
    // the map, a tab switch, or the last capture differed), and at
    // IdleIntervalUsec otherwise, to notice changes nobody announced (a map
    // animation). Each capture is a full render and readback, so idle checks
    // are rare; piles announce their own changes (see DsGameHooks).
    private const ulong IdleIntervalUsec = 1_000_000;

    // A capture renders on the game's GPU in the game's frame. After a slow
    // game frame (over SlowFrameUsec), one that is due waits for a faster
    // frame, but never longer than MaxDeferUsec. A touch never waits.
    private const ulong SlowFrameUsec = 20_000;
    private const ulong MaxDeferUsec = 250_000;
    private ulong _deferredSinceUsec;

    // While the game boots there is nothing to show below, and the menu art
    // load and its captures would compete with the boot: one black frame,
    // then nothing until the main menu is up (or BootTimeoutUsec, should
    // that hook never fire).
    private const ulong BootTimeoutUsec = 30_000_000;
    private readonly ulong _createdUsec;
    private bool _booted;
    private bool _bootFrameShown;
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
    private ulong _lastFrameDtUsec;
    private ulong _frameUsecTotal;
    private ulong _frameUsecMax;
    private int _frameCount;
    private int _hitches;
    private int _deferred;
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

    // A touch on a pile turns into a scroll once it moves past the slop;
    // until then it can still be a tap on a card. Let go fast and it flings.
    private const float DragSlop = 14f;
    private const float FlingDecay = 4.5f;
    private const float FlingMin = 40f;
    private Vector2 _dragOrigin;
    private bool _pileDrag;
    private float _dragVelocity;
    private ulong _lastDragUsec;
    private float _fling;
    private ulong _lastFlingUsec;

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

        // Shown outside a run: black, with the menu art over it once loaded.
        var blank = new ColorRect { Name = "Blank", Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        blank.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _viewport.AddChild(blank);
        _idle = new Control { Name = "Idle", ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        _idle.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _viewport.AddChild(_idle);

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

        _createdUsec = Time.GetTicksUsec();
        _rd = RenderingServer.GetRenderingDevice();
        // Variant, not byte[]: the frame arrives as Godot's own refcounted
        // PackedByteArray and is read in place. A byte[] would be a fresh
        // 5 MB managed array per capture, and that churn forces full GCs.
        _onFrameData = Callable.From<Variant>(OnFrameData);
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
        LayoutIdleArt();
    }

    // ── idle art ───────────────────────────────────────────────────────────

    private void UpdateIdleArt()
    {
        if (_idleArt != null || _idleArtFailed)
            return;
        try
        {
            if (!_idleArtLoading)
            {
                if (!ResourceLoader.Exists(IdleArtScene) || ResourceLoader.LoadThreadedRequest(IdleArtScene) != Error.Ok)
                {
                    _idleArtFailed = true;
                    DsLog.Info($"menu art not available ({IdleArtScene}); lower screen stays black outside a run");
                    return;
                }
                _idleArtLoading = true;
                return;
            }
            var status = ResourceLoader.LoadThreadedGetStatus(IdleArtScene);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress)
                return;
            _idleArtLoading = false;
            if (status != ResourceLoader.ThreadLoadStatus.Loaded || ResourceLoader.LoadThreadedGet(IdleArtScene) is not PackedScene scene)
            {
                _idleArtFailed = true;
                DsLog.Info($"menu art failed to load ({status})");
                return;
            }
            var art = scene.Instantiate<Control>();
            var holder = new Control { Name = "MenuArt", MouseFilter = Control.MouseFilterEnum.Ignore, Size = IdleArtSize };
            holder.AddChild(art);
            art.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            art.MouseFilter = Control.MouseFilterEnum.Ignore;
            _idle.AddChild(holder);
            _idleArt = holder;
            _idleArtShownUsec = Time.GetTicksUsec();
            _idleArtFrozen = false;
            LayoutIdleArt();
            MarkChanged();
            DsLog.Info("menu art shown on the lower screen");
        }
        catch (Exception e)
        {
            _idleArtFailed = true;
            _idleArtLoading = false;
            DsLog.Info($"menu art failed: {e.Message}");
        }
    }

    // Cover the canvas, centred: the art's middle is what matters.
    private void LayoutIdleArt()
    {
        if (_idleArt == null || !GodotObject.IsInstanceValid(_idleArt))
            return;
        var canvas = CanvasSize;
        var scale = Math.Max(canvas.X / IdleArtSize.X, canvas.Y / IdleArtSize.Y);
        _idleArt.Scale = new Vector2(scale, scale);
        _idleArt.Position = (canvas - IdleArtSize * scale) / 2f;
    }

    private void FreezeIdleArt()
    {
        if (_idleArt == null || _idleArtFrozen || Time.GetTicksUsec() - _idleArtShownUsec < IdleArtSettleUsec)
            return;
        _idleArtFrozen = true;
        _idleArt.ProcessMode = Node.ProcessModeEnum.Disabled;
        // One last capture of the still.
        MarkChanged();
    }

    private void FreeIdleArt()
    {
        _idleArtFrozen = false;
        if (_idleArt != null && GodotObject.IsInstanceValid(_idleArt))
            _idleArt.QueueFree();
        _idleArt = null;
    }

    // ── controller ─────────────────────────────────────────────────────────

    // Emitted for every event the root window gets, before the root handles
    // it; that includes the ui_* actions the game makes from controller
    // buttons with Input.ParseInputEvent.
    private void OnRootInput(InputEvent e)
    {
        DsGameHooks.NoteInput(e);
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
                _bootFrameShown = false;
                MarkChanged();
                DsLog.Info($"bottom surface {state.Width}x{state.Height}, canvas {_viewport.Size2DOverride}");
                DsGameHooks.OnSurfaceReady();
            }

            UpdateRunState();
            var booting = IsBooting();
            if (!_inRun && !booting)
            {
                UpdateIdleArt();
                FreezeIdleArt();
            }
            PumpTouches();
            UpdateFling();

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
            // The frozen menu art never changes by itself: no idle checks.
            var still = booting ? _bootFrameShown : !_inRun && _idleArtFrozen && now >= _activeUntilUsec;
            if (now >= _nextFrameUsec && _inFlight < MaxInFlight && !still && !DeferCapture(now))
            {
                // The menu art animates without pause; it needn't be smooth.
                // A pile grid is many cards, each costly to draw, and mostly
                // still; only the map gets the full rate.
                var fps = !_inRun ? Math.Min(_config.Fps, IdleArtFps)
                    : _active == "map" || TouchRecent || _fling != 0 ? _config.Fps
                    : Math.Min(_config.Fps, DsGameHooks.InCombat ? CombatFps : PileFps);
                var interval = now < _activeUntilUsec ? (ulong)(1_000_000 / Math.Max(1, fps)) : IdleIntervalUsec;
                _nextFrameUsec = now + interval;
                RenderingServer.ViewportSetUpdateMode(_viewport.GetViewportRid(), RenderingServer.ViewportUpdateMode.Once);
                _readbackDue = true;
                if (booting)
                    _bootFrameShown = true;
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

    private bool IsBooting()
    {
        if (_booted)
            return false;
        var mainMenu = DsGameHooks.MainMenuReady;
        if (!_inRun && !mainMenu && Time.GetTicksUsec() - _createdUsec < BootTimeoutUsec)
            return true;
        _booted = true;
        if (!_inRun && !mainMenu)
            DsLog.Info("no main menu after 30 s; lower screen wakes anyway");
        MarkChanged();
        return false;
    }

    private bool DeferCapture(ulong now)
    {
        if (_lastFrameDtUsec <= SlowFrameUsec || TouchRecent)
        {
            _deferredSinceUsec = 0;
            return false;
        }
        if (_deferredSinceUsec == 0)
            _deferredSinceUsec = now;
        if (now - _deferredSinceUsec >= MaxDeferUsec)
        {
            _deferredSinceUsec = 0;
            return false;
        }
        _deferred++;
        return true;
    }

    private void UpdateRunState()
    {
        var inRun = DsGameHooks.InRun();
        if (inRun == _inRun)
            return;
        _inRun = inRun;
        _root.Visible = inRun;
        _idle.Visible = !inRun;
        if (inRun)
            FreeIdleArt();
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
            _lastFrameDtUsec = dt;
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
        DsLog.Info($"frames posted={stats.Posted} unchanged={stats.Skipped} touch_overflows={stats.Overflows} readback_avg={readback:F1}ms submit_avg={submit:F1}ms captures={_readbackCount} tab={_active} combat={DsGameHooks.InCombat} deferred={_deferred}"
            + $" | game frame_avg={frame:F1}ms frame_max={_frameUsecMax / 1000.0:F1}ms hitches={_hitches}/{_frameCount} gc_minor=+{gcMinor - _gcMinor} gc_major=+{gcMajor - _gcMajor}");
        _readbackMsTotal = _submitMsTotal = 0;
        _readbackCount = _submitCount = 0;
        _frameUsecTotal = _frameUsecMax = 0;
        _frameCount = _hitches = _deferred = 0;
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

    private unsafe void OnFrameData(Variant data)
    {
        if (_inFlight > 0)
            _inFlight--;
        // Release the frame now, not when a finalizer gets to it.
        using var frame = data;
        if (!_surfaceReady)
            return;
        var width = _requestWidth;
        var height = _requestHeight;
        var native = frame.CopyNativeVariant();
        var bytes = NativeFuncs.godotsharp_variant_as_packed_byte_array(in native);
        try
        {
            if (bytes.Buffer == null || bytes.Size != width * height * 4)
                return; // resized while in flight
            Submit(bytes.Buffer, width, height);
        }
        finally
        {
            bytes.Dispose();
            native.Dispose();
        }
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
        fixed (byte* pixels = data)
        {
            Submit(pixels, width, height);
        }
    }

    private unsafe void Submit(byte* pixels, int width, int height)
    {
        var started = Time.GetTicksUsec();
        var posted = DsNative.SubmitFrame(pixels, width, height, width * 4);
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
                        _dragOrigin = pos;
                        _pileDrag = false;
                        _dragVelocity = 0;
                        _fling = 0;
                        Push(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                        Push(MouseButtonEvent(pos, pressed: true));
                    }
                    else if (_fingers.Count == 2)
                    {
                        // A second finger turns the gesture into a scroll: let go
                        // of the first finger's press (ending a drawn line or a
                        // drag) and follow the midpoint from here.
                        if (_pointerId >= 0 && !_pileDrag)
                            Push(MouseButtonEvent(_pointerPos, pressed: false));
                        if (_pileDrag)
                            DsGameHooks.HoldPile(_active, false);
                        _pileDrag = false;
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
                    if (_active != "map" && DragPile(pos, relative))
                        break;
                    Push(new InputEventMouseMotion
                    {
                        Position = pos,
                        GlobalPosition = pos,
                        Relative = relative,
                        ButtonMask = MouseButtonMask.Left,
                    });
                    break;
                case DsNative.PhaseUp:
                case DsNative.PhaseCancel:
                    _fingers.Remove(t.Id);
                    if (t.Id == _pointerId)
                    {
                        if (_pileDrag)
                            ReleasePile();
                        else
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
        else if (!DsGameHooks.ScrollPile(_active, dy))
            EmulateWheel(mid, dy);
    }

    // Past the slop, the touch lets go of whatever card it pressed (off
    // screen, so no card counts it as a tap) and moves the pile instead.
    private bool DragPile(Vector2 pos, Vector2 relative)
    {
        if (!_pileDrag)
        {
            if ((pos - _dragOrigin).Length() < DragSlop || !DsGameHooks.HoldPile(_active, true))
                return false;
            _pileDrag = true;
            var away = new Vector2(-10000f, -10000f);
            Push(new InputEventMouseMotion { Position = away, GlobalPosition = away });
            Push(MouseButtonEvent(away, pressed: false));
            relative = pos - _dragOrigin;
            _lastDragUsec = Time.GetTicksUsec();
        }
        DsGameHooks.ScrollPile(_active, relative.Y);
        var now = Time.GetTicksUsec();
        var dt = Math.Max(1e-3f, (now - _lastDragUsec) / 1e6f);
        _lastDragUsec = now;
        _dragVelocity = Mathf.Lerp(_dragVelocity, relative.Y / dt, 0.4f);
        return true;
    }

    private void ReleasePile()
    {
        _pileDrag = false;
        DsGameHooks.HoldPile(_active, false);
        // A finger that stopped before lifting doesn't fling.
        var still = Time.GetTicksUsec() - _lastDragUsec > 80_000;
        _fling = still || Math.Abs(_dragVelocity) < FlingMin * 4 ? 0 : _dragVelocity;
        _lastFlingUsec = Time.GetTicksUsec();
    }

    private void UpdateFling()
    {
        if (_fling == 0)
            return;
        var now = Time.GetTicksUsec();
        var dt = (now - _lastFlingUsec) / 1e6f;
        _lastFlingUsec = now;
        if (_active == "map" || _pointerId >= 0 || !DsGameHooks.ScrollPile(_active, _fling * dt))
        {
            _fling = 0;
            return;
        }
        _fling *= MathF.Exp(-FlingDecay * dt);
        if (Math.Abs(_fling) < FlingMin)
            _fling = 0;
        MarkChanged();
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
