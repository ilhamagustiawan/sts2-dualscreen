using System;
using System.Collections.Generic;
using Godot;

namespace STS2DualScreen;

// The bottom screen.
//
// Four SubViewports, each captured on its own (DsCapture) and handed to
// libsts2ds as a region of the surface, posted together once a frame: the
// pages above, the combat piles' tab strip (DsDock) along the bottom edge,
// and the map's and the deck's tabs as plates in the top corners, laid over
// the pages with alpha (as the game puts those buttons up top). Pages host the game's own nodes (its map
// screen, its card pile screens), moved or instantiated here by DsGameHooks,
// so they look and behave as they do on PC. The dock's endless motion costs
// only its own small strip; the pages render only when a capture is due (see
// MarkChanged). Touches queued by the Presentation come back as mouse events
// pushed into whichever viewport they landed on. This screen is for touch
// only: each of those runs with the game in touch mode, even while the top
// screen is in controller mode (DsGameHooks.BeginTouchDispatch). Controller
// and keyboard input stays with the game on the top screen.
//
// Outside a run (boot, main menu) the screen shows the game's own animated
// main-menu art at half resolution over the whole surface and ignores
// touches; the tabs appear once a run is loaded.
//
// Things that loop forever (the dock, the menu art, the open map's pulsing
// rooms) are captured at an ambient rate, which steps down while the game
// is dropping frames.
//
// Nothing here subclasses a Godot type: this assembly is built without the
// Godot source generators, so virtual callbacks like _Process would never
// fire. Everything runs off SceneTree.ProcessFrame and signals instead.
internal sealed class DsBottomScreen
{
    public static DsBottomScreen Instance { get; private set; }

    public static readonly string[] Tabs = { "map", "draw", "discard", "exhaust", "deck" };
    private static readonly string[] StripTabs = { "draw", "discard", "exhaust" };
    public const float TabBarHeight = 150f;

    // A part of the screen drawn and captured on its own: the strip or a
    // corner plate. Px is where it goes on the surface.
    private sealed class DockPart
    {
        public SubViewport Viewport;
        public DsCapture Capture;
        public Control Root;
        public DsDock Dock;
        public int Region;
        public Rect2I Px;
    }

    private readonly DsConfig _config;
    private readonly SceneTree _tree;
    private readonly SubViewport _viewport;
    private readonly DsCapture _page;
    private readonly Control _root;
    private readonly DockPart _strip, _mapCorner, _deckCorner;
    private readonly DockPart[] _parts;
    private readonly DsDockSet _dock;
    private readonly Control _content;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, Control> _placeholders = new();
    private readonly Dictionary<string, Label> _placeholderLabels = new();
    private readonly Dictionary<string, TextureRect> _placeholderIcons = new();
    private readonly CanvasLayer _hoverLayer;
    private Tween _pageTween, _runTween;
    private bool _fontApplied;
    private string _active;
    private int _generation = -1;
    private bool _surfaceReady;
    private bool _inRun;

    // Surface layout, in surface pixels: the pages over the top _pagePx rows,
    // the dock under them (in a run only).
    private int _surfaceW = 1240, _surfaceH = 1080;
    private int _pagePx = 1080;

    // The main menu's backdrop scene (Spine art, particles, the logo), drawn
    // for 1920x1080 and scaled to cover the canvas. Loaded on a thread while
    // outside a run and freed when one starts, so its textures don't stay
    // resident during play. It keeps playing, rendered at half resolution
    // and captured at the ambient rate.
    private const string IdleArtScene = "res://scenes/backgrounds/main_menu_bg.tscn";
    private static readonly Vector2 IdleArtSize = new(1920, 1080);
    private const int IdleArtFps = 24;
    private const int PileFps = 30;
    // In a fight every card move redraws a pile grid while the top screen is
    // busiest; half the rate there is still smooth enough for cards moving.
    private const int CombatFps = 15;
    // Until then a one-shot animation is playing on the pages (cards moving
    // in a pile): outside a fight the caps give way to the set rate while it
    // runs (see PileRate).
    private ulong _pageMotionUntilUsec;
    private Control _idle;
    private Control _idleArt;
    private bool _idleArtLoading;
    private bool _idleArtFailed;
    private ulong _lastStatsUsec;

    // A capture renders on the game's GPU in the game's frame. After a slow
    // game frame (over SlowFrameUsec), one that is due waits for a faster
    // frame, but never longer than MaxDeferUsec. A touch or scroll never waits.
    private const ulong SlowFrameUsec = 20_000;
    private const ulong MaxDeferUsec = 250_000;
    private ulong _deferredSinceUsec;
    private int _deferred;

    // While the game boots there is nothing to show below, and the menu art
    // load and its captures would compete with the boot: one black frame,
    // then nothing until the main menu is up (or BootTimeoutUsec, should
    // that hook never fire).
    private const ulong BootTimeoutUsec = 30_000_000;
    private readonly ulong _createdUsec;
    private bool _booted;
    private bool _bootFrameShown;
    private ulong _wokeUsec;

    // Woken, the menu art still waits for the game's Common preload (see
    // DsStartup), which a run waits on; at most this long.
    private const ulong MenuArtWaitUsec = 20_000_000;

    // Ambient capture rates, by budget level. The level goes up while the
    // game drops frames and back down once it has been clean for a while.
    private static readonly int[] PageAmbientFps = { IdleArtFps, 16, 12, 8 };
    private static readonly int[] DockAmbientFps = { 30, 20, 15, 10 };
    private const ulong BudgetWindowUsec = 2_000_000;
    private const ulong BudgetRecoverUsec = 4_000_000;
    private int _budget;
    private ulong _budgetStartUsec;
    private int _budgetFrames, _budgetHitches;
    private ulong _budgetCleanUsec;
    private double _frameUsecAverage;

    // Game frame pacing, for the stats line.
    private ulong _lastFrameUsec;
    private ulong _lastFrameDtUsec;
    private ulong _frameUsecTotal;
    private ulong _frameUsecMax;
    private int _frameCount;
    private int _hitches;
    private int _gcMinor;
    private int _gcMajor;

    // Touch → mouse. A gesture belongs to the viewport it went down on.
    private int _pointerId = -1;
    private Vector2 _pointerPos;
    private Vector2 _pushedPos;
    // The dock part the gesture went down on; null for the pages.
    private DockPart _pointerPart;
    private ulong _lastTouchUsec;
    private readonly Dictionary<int, Vector2> _fingers = new();
    private bool _twoFinger;
    private Vector2 _scrollMid;

    // Scrolling. A touch on the map or a pile turns into a scroll once it
    // moves past the slop; until then it can still be a tap. The scroll moves
    // the map or pile directly (DsGameHooks.ScrollBy), once per frame with
    // everything that moved since, so the content stays under the finger.
    // Let go fast and it flings. While scrolling the pages capture at
    // ScrollFps whatever the setting: it is brief, and it is where lag shows.
    private const float DragSlop = 14f;
    private const float FlingDecay = 4.5f;
    private const float FlingMin = 40f;
    private const int ScrollFps = 60;
    private const ulong ScrollTailUsec = 250_000;
    private Vector2 _dragOrigin;
    private bool _scrollDrag;
    private bool _heldScroll;
    private float _pendingDy;
    private bool _releaseDue;
    private bool _velocityPrimed;
    private float _scrollVelocity;
    private ulong _lastScrollUsec;
    private ulong _scrollingUntilUsec;
    private float _fling;
    private ulong _lastFlingUsec;

    // Drawing. With a drawing tool on, a finger on the map draws, and two
    // fingers scroll it. The pen goes down only once the finger has moved
    // a little or held still for a moment: a second finger arriving first
    // makes it a scroll that leaves no mark. The line still starts where
    // the finger first touched.
    private const ulong PenDelayUsec = 70_000;
    private const float PenSlop = 6f;
    private bool _penPending;
    private ulong _penDownUsec;

    // The touch let go of what it pressed but could not scroll; the rest of
    // it does nothing.
    private bool _gestureDead;

    public bool Ready => _surfaceReady;

    /// <summary>A touch on the lower screen is down or ended in the last moment,
    /// so whatever it pressed was pressed by touch, not the controller.</summary>
    public bool TouchRecent => _pointerId >= 0 || Time.GetTicksUsec() - _lastTouchUsec < 300_000;

    /// <summary>A finger is on the pages, or they are still moving from one.</summary>
    public bool Touching => TouchRecent || _twoFinger || _fling != 0;

    /// <summary>The pages' canvas: everything above the dock.</summary>
    public Vector2 CanvasSize => _viewport.Size2DOverride;

    /// <summary>Hover tips for nodes on this screen, above the pages.</summary>
    public CanvasLayer HoverLayer => _hoverLayer;

    public DsDockSet Dock => _dock;

    public bool IsBottom(Node node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return false;
        var viewport = node.GetViewport();
        return viewport == _viewport || PartOf(viewport) != null;
    }

    private DsBottomScreen(SceneTree tree, DsConfig config)
    {
        _tree = tree;
        _config = config;

        _viewport = NewViewport("DsBottomScreen");

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
        _content.MouseFilter = Control.MouseFilterEnum.Pass;
        _root.AddChild(_content);

        var rd = RenderingServer.GetRenderingDevice();
        _strip = NewPart("DsDock", 1, StripTabs, DockShape.Strip, rd);
        _mapCorner = NewPart("DsMapTab", 2, new[] { "map" }, DockShape.CornerLeft, rd);
        _deckCorner = NewPart("DsDeckTab", 3, new[] { "deck" }, DockShape.CornerRight, rd);
        _parts = new[] { _strip, _mapCorner, _deckCorner };
        _dock = new DsDockSet(_strip.Dock, _mapCorner.Dock, _deckCorner.Dock);

        foreach (var tab in Tabs)
        {
            var page = new Control { Name = $"Page_{tab}", Visible = false, MouseFilter = Control.MouseFilterEnum.Pass };
            page.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _content.AddChild(page);
            _pages[tab] = page;
            BuildPlaceholder(tab, page);
        }
        RefreshPlaceholders();

        _hoverLayer = new CanvasLayer { Name = "HoverTips", Layer = 100 };
        _viewport.AddChild(_hoverLayer);

        _createdUsec = Time.GetTicksUsec();
        DsCapture.Stagger = config.StaggerCaptures;
        StartPresentThread(config.PresentThread);
        _page = new DsCapture("page", _viewport, rd);
        DsLog.Info(rd != null ? "bottom readback: async (RenderingDevice)" : "bottom readback: inline (no RenderingDevice)");
        ApplyLayout(_surfaceW, _surfaceH);

        _tree.Root.AddChild(_viewport);
        foreach (var part in _parts)
            _tree.Root.AddChild(part.Viewport);
        _tree.ProcessFrame += OnProcessFrame;
        RenderingServer.FramePostDraw += OnPostDraw;
        _tree.Root.WindowInput += OnRootInput;
        Show(Array.IndexOf(Tabs, config.DefaultTab) >= 0 ? config.DefaultTab : "map");
    }

    private static void StartPresentThread(bool on)
    {
        if (!on)
        {
            DsLog.Info("bottom present: on the game's thread (present_thread off)");
            return;
        }
        try
        {
            DsLog.Info(DsNative.PresentThread(true)
                ? "bottom present: native thread"
                : "bottom present: native thread failed to start; on the game's thread");
        }
        catch (Exception e)
        {
            // An older libsts2ds without the thread.
            DsLog.Info($"bottom present: on the game's thread ({e.GetType().Name})");
        }
    }

    private static SubViewport NewViewport(string name) =>
        new()
        {
            Name = name,
            Disable3D = true,
            TransparentBg = false,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Size2DOverrideStretch = true,
        };

    private DockPart NewPart(string name, int region, string[] tabs, DockShape shape, RenderingDevice rd)
    {
        var viewport = NewViewport(name);
        // The corner plates are laid over the pages: their background is see-through.
        viewport.TransparentBg = shape != DockShape.Strip;
        var root = new Control { Name = "Dock", MouseFilter = Control.MouseFilterEnum.Pass };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        viewport.AddChild(root);
        return new DockPart
        {
            Viewport = viewport,
            Root = root,
            Region = region,
            Dock = new DsDock(root, tabs, OnTabPressed, shape),
            Capture = new DsCapture(name, viewport, rd) { Enabled = false },
        };
    }

    private DockPart PartOf(Viewport viewport)
    {
        foreach (var part in _parts)
        {
            if (part.Viewport == viewport)
                return part;
        }
        return null;
    }

    /// <summary>Opens a trace section for a Perfetto capture; close it with TraceEnd, same thread. No-op without libsts2ds.</summary>
    public static void TraceBegin(string name) => DsNative.TraceBegin(name);

    public static void TraceEnd() => DsNative.TraceEnd();

    /// <summary>A pile was rebuilt (called once per real rebuild): counted for the stats line.</summary>
    public static void NotePileRebuild() => DsPerf.NotePileRebuild();

    public static void Create(SceneTree tree, DsConfig config)
    {
        if (Instance != null)
            return;
        Instance = new DsBottomScreen(tree, config);
        DsLog.Info("bottom screen host created");
        DsGameHooks.OnBottomScreenReady(Instance);
    }

    public Control Page(string tab) => _pages[tab];

    // ── placeholders ───────────────────────────────────────────────────────

    // The tab's icon, dimmed, over a line in the game's font.
    private void BuildPlaceholder(string tab, Control page)
    {
        var box = new CenterContainer { Name = "Placeholder", MouseFilter = Control.MouseFilterEnum.Ignore };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 18);
        box.AddChild(column);
        var icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(130, 130),
            Modulate = new Color(1f, 1f, 1f, 0.3f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        column.AddChild(icon);
        var label = DsStyle.Text(40, 8);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(900, 0);
        label.Modulate = DsStyle.Cream with { A = 0.8f };
        column.AddChild(label);
        page.AddChild(box);
        _placeholders[tab] = box;
        _placeholderLabels[tab] = label;
        _placeholderIcons[tab] = icon;
    }

    /// <summary>What an empty tab says, with the game's name for it.</summary>
    public string PlaceholderText(string tab) =>
        tab is "draw" or "discard" or "exhaust"
            ? $"{_dock.Title(tab)} shows here during combat."
            : $"{_dock.Title(tab)} shows here once a run starts.";

    private void RefreshPlaceholders()
    {
        foreach (var (tab, label) in _placeholderLabels)
        {
            if (_placeholders[tab].Visible)
                label.Text = PlaceholderText(tab);
        }
    }

    public void SetPlaceholder(string tab, string text)
    {
        var box = _placeholders[tab];
        var show = !string.IsNullOrEmpty(text);
        _placeholderLabels[tab].Text = text ?? "";
        _placeholderIcons[tab].Texture = _dock.IconOf(tab);
        if (show && !box.Visible && tab == _active && _inRun)
        {
            box.Modulate = box.Modulate with { A = 0f };
            var fade = DsStyle.Animate(box, 0.4);
            fade.TweenProperty(box, "modulate:a", 1f, 0.4).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
        else if (show)
        {
            box.Modulate = Colors.White;
        }
        box.Visible = show;
        MarkChanged();
    }

    // ── capture pacing ─────────────────────────────────────────────────────

    /// <summary>Capture the pages at full rate for a while: something on them is moving.</summary>
    public void MarkChanged() => _page.MarkChanged();

    /// <summary>Capture whichever part owner is on at full rate for this long.</summary>
    public void MarkChanged(Node owner, double seconds)
    {
        var viewport = owner != null && GodotObject.IsInstanceValid(owner) && owner.IsInsideTree() ? owner.GetViewport() : null;
        if (PartOf(viewport) is { } part)
        {
            part.Capture.MarkChanged(seconds);
        }
        else if (viewport == _viewport || viewport == null)
        {
            _page.MarkChanged(seconds);
            // Loops only keep their part awake (0.1 s); anything longer is a one-shot.
            if (seconds > 0.1)
                _pageMotionUntilUsec = Math.Max(_pageMotionUntilUsec, Time.GetTicksUsec() + (ulong)(seconds * 1_000_000));
        }
    }

    private bool Scrolling(ulong now) => _scrollDrag || _twoFinger || _fling != 0 || now < _scrollingUntilUsec;

    // Something on the pages loops forever: the menu art, or the open map's
    // travelable rooms pulsing (the game's own NNormalMapPoint animation).
    private bool PageAmbient() =>
        !_inRun ? _idleArt != null : _active == "map" && DsGameHooks.MapAmbient();

    // ── tabs ───────────────────────────────────────────────────────────────

    public void Show(string tab)
    {
        if (!_pages.ContainsKey(tab))
            return;
        var changed = tab != _active;
        var animate = changed && _inRun && _surfaceReady;
        if (changed && _active != null)
            StopScroll();
        var previous = _active;
        _active = tab;
        foreach (var (key, page) in _pages)
        {
            page.Visible = key == tab;
            // A hidden page's game nodes still ran every frame: the map's
            // reachable rooms pulse in _Process and its tweens and the grids'
            // tick. Paused, they pick up where they were once shown. The
            // mirroring (SetMap, travel state, drawings) is plain calls and
            // signals, which a paused node still takes.
            page.ProcessMode = key == tab || !_config.PauseHiddenPages
                ? Node.ProcessModeEnum.Inherit
                : Node.ProcessModeEnum.Disabled;
        }
        _dock.Select(tab, animate);
        MarkChanged();
        DsGameHooks.OnTabShown(tab);
        if (animate)
            AnimatePageIn(previous, tab);
    }

    public string ActiveTab => _active;

    // A tab tapped (after the dock's click): a page opening sounds as the
    // game's own screen does. Switches the game makes itself (a fight
    // starting or ending) stay quiet; the top screen has its own sounds then.
    private void OnTabPressed(string tab)
    {
        if (tab != _active && _inRun && _pages.ContainsKey(tab))
            DsStyle.OpenSound(tab);
        Show(tab);
    }

    // The pile screens' backdrop and the card library's grid: the page fades
    // in and a pile's cards rise into place one after another. The page
    // itself only fades, so the cards aren't moved twice (and the map's
    // scroll is never fought).
    private void AnimatePageIn(string previous, string tab)
    {
        DsStyle.Kill(ref _pageTween);
        if (previous != null && _pages.TryGetValue(previous, out var old))
        {
            old.Modulate = Colors.White;
            old.Position = Vector2.Zero;
        }
        var page = _pages[tab];
        page.Position = Vector2.Zero;
        page.Modulate = page.Modulate with { A = 0f };
        _pageTween = DsStyle.Animate(page, 0.4).SetParallel(true);
        _pageTween.TweenProperty(page, "modulate:a", 1f, 0.25).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        if (tab != "map")
            Callable.From(() => DsGameHooks.StaggerPile(tab)).CallDeferred();
    }

    // ── text style ─────────────────────────────────────────────────────────

    /// <summary>
    /// The game's own label font (from a live label, so it follows the
    /// game's per-language font swap) for the dock and the placeholders.
    /// </summary>
    public void ApplyGameFont(Font font)
    {
        if (_fontApplied || font == null)
            return;
        _fontApplied = true;
        _dock.ApplyFont(font);
        foreach (var label in _placeholderLabels.Values)
            label.AddThemeFontOverride("font", font);
        DsLog.Info($"game font applied to the lower screen ({font.ResourcePath})");
    }

    // ── layout ─────────────────────────────────────────────────────────────

    private void ApplyLayout(int width, int height)
    {
        _surfaceW = width;
        _surfaceH = height;
        var scale = _config.CanvasScale;
        if (_inRun)
        {
            var stripPx = Math.Clamp((int)MathF.Round(TabBarHeight / scale), 1, Math.Max(1, height / 2));
            _pagePx = height - stripPx;
            SetSize(_viewport, new Vector2I(width, _pagePx), scale);
            _page.TargetRegion(0, 0, 0);
            PlacePart(_strip, new Rect2I(0, _pagePx, width, stripPx), scale);
            var corner = new Vector2I(
                Math.Clamp((int)MathF.Round(DsDock.CornerSize.X / scale), 1, Math.Max(1, width / 3)),
                Math.Clamp((int)MathF.Round(DsDock.CornerSize.Y / scale), 1, Math.Max(1, _pagePx / 3)));
            PlacePart(_mapCorner, new Rect2I(0, 0, corner.X, corner.Y), scale);
            PlacePart(_deckCorner, new Rect2I(width - corner.X, 0, corner.X, corner.Y), scale);
        }
        else
        {
            // Half the pixels each way, the same canvas: a quarter of the
            // drawing and copying, and the compositor scales it up.
            _pagePx = height;
            _viewport.Size = new Vector2I(Math.Max(1, width / 2), Math.Max(1, height / 2));
            _viewport.Size2DOverride = new Vector2I((int)MathF.Round(width * scale), (int)MathF.Round(height * scale));
            _page.TargetWhole();
        }
        _page.Retarget();
        foreach (var part in _parts)
            part.Capture.Retarget();
        LayoutIdleArt();
    }

    private static void PlacePart(DockPart part, Rect2I px, float scale)
    {
        part.Px = px;
        SetSize(part.Viewport, px.Size, scale);
        part.Capture.TargetRegion(px.Position.X, px.Position.Y, part.Region);
    }

    private static void SetSize(SubViewport viewport, Vector2I size, float scale)
    {
        viewport.Size = size;
        viewport.Size2DOverride = new Vector2I((int)MathF.Round(size.X * scale), (int)MathF.Round(size.Y * scale));
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
            LayoutIdleArt();
            holder.Modulate = holder.Modulate with { A = 0f };
            var fade = DsStyle.Animate(holder, 0.6);
            fade.TweenProperty(holder, "modulate:a", 1f, 0.6).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
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

    private void FreeIdleArt()
    {
        if (_idleArt != null && GodotObject.IsInstanceValid(_idleArt))
            _idleArt.QueueFree();
        _idleArt = null;
    }

    // ── top-screen input ───────────────────────────────────────────────────

    // Emitted for every event the root window gets, before the root handles
    // it; that includes the ui_* actions the game makes from controller
    // buttons with Input.ParseInputEvent. Only noted, never handled here.
    private void OnRootInput(InputEvent e) => DsGameHooks.NoteInput(e);

    // ── per frame ──────────────────────────────────────────────────────────

    private void OnProcessFrame()
    {
        DsNative.TraceBegin("ds tick");
        try
        {
            Tick();
        }
        finally
        {
            DsNative.TraceEnd();
        }
    }

    private void Tick()
    {
        try
        {
            // One frame may go out to libsts2ds again (DsCapture.Stagger),
            // even in a frame that renders nothing.
            DsCapture.BeginFrame();
            var now = Time.GetTicksUsec();
            TrackFramePacing(now);
            var state = DsNative.State();
            if (!state.Ready)
            {
                if (_surfaceReady)
                {
                    _surfaceReady = false;
                    _page.Retarget();
                    foreach (var part in _parts)
                        part.Capture.Retarget();
                    DsLog.Info("bottom surface gone; paused");
                }
                return;
            }
            if (!_surfaceReady || state.Generation != _generation)
            {
                _surfaceReady = true;
                _generation = state.Generation;
                ApplyLayout(state.Width, state.Height);
                _pointerId = -1;
                _fingers.Clear();
                _twoFinger = false;
                _bootFrameShown = false;
                StopScroll();
                MarkChanged();
                DsLog.Info($"bottom surface {state.Width}x{state.Height}, pages {_viewport.Size} (canvas {_viewport.Size2DOverride}), dock {_strip.Viewport.Size}, corners {_mapCorner.Viewport.Size}");
                DsGameHooks.OnSurfaceReady();
            }

            UpdateRunState();
            var booting = IsBooting();
            if (!_inRun && !booting && (DsStartup.CommonPreloaded || now - _wokeUsec > MenuArtWaitUsec))
                UpdateIdleArt();
            PumpTouches(now);
            UpdateFling(now);

            var pageAmbient = !booting && PageAmbient();
            var dockAmbient = _inRun && _dock.Animating;
            UpdateBudget(now, pageAmbient || dockAmbient || now < _pageMotionUntilUsec);

            // The menu art needn't be smooth; a pile grid is many cards, each
            // costly to draw, and mostly still (more so in a fight); the map
            // gets the set rate, and a scroll more than that.
            var scrolling = Scrolling(now);
            var fps = scrolling ? Math.Max(_config.Fps, ScrollFps)
                : !_inRun ? Math.Min(_config.Fps, IdleArtFps)
                : _active == "map" || TouchRecent ? _config.Fps
                : PileRate(now);
            var defer = DeferCapture(now, scrolling);
            if (!defer)
                RunCaptures(now, fps, booting, pageAmbient, urgent: scrolling || TouchRecent && _pointerPart == null);

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

    // A pile page. Cards moving in it lift the rate to the set one, but not
    // in a fight: there the top screen is at its busiest just as cards move,
    // so motion gets CombatMotionFps at most. While the game is dropping
    // frames already, the budget's lower rate holds, motion or not.
    private int PileRate(ulong now)
    {
        var motion = now < _pageMotionUntilUsec;
        var fps = DsGameHooks.InCombat
            ? Math.Min(_config.Fps, motion ? Math.Max(CombatFps, _config.CombatMotionFps) : CombatFps)
            : motion ? _config.Fps : Math.Min(_config.Fps, PileFps);
        return _budget > 0 ? Math.Min(fps, PageAmbientFps[_budget]) : fps;
    }

    // Each part renders only when its capture is due, and each render costs
    // the game's GPU in the game's frame, then a full copy when it comes
    // back. So at most one part starts a render a game frame (the one that
    // has waited longest; the pages first while a finger is on them), and at
    // most one copy goes out (DsCapture.Stagger): bursts are spread over
    // frames instead of landing in one.
    private void RunCaptures(ulong now, int fps, bool booting, bool pageAmbient, bool urgent)
    {
        if (DsCapture.Stagger)
        {
            // A frame held back last game frame goes out first, oldest first.
            DsCapture held = _page.HasPending ? _page : null;
            foreach (var part in _parts)
            {
                if (part.Capture.HasPending && (held == null || part.Capture.PendingSeq < held.PendingSeq))
                    held = part.Capture;
            }
            held?.SubmitPending();
        }
        _page.Tick(now);
        foreach (var part in _parts)
            part.Capture.Tick(now);

        var stagger = _config.StaggerCaptures;
        DsCapture next = null;
        var nextSince = ulong.MaxValue;
        // Booting: one black frame, then nothing.
        if (!(booting && _bootFrameShown))
        {
            if (_page.Due(now, fps, pageAmbient ? PageAmbientFps[_budget] : 0, idleChecks: !booting, out var since))
            {
                if (stagger)
                {
                    next = _page;
                    nextSince = urgent ? 0 : since;
                }
                else
                {
                    _page.Start(now);
                }
            }
            if (booting)
                _bootFrameShown = true;
        }
        // The dock changes only by its own animations, which say so.
        foreach (var part in _parts)
        {
            if (!part.Capture.Due(now, _config.Fps, _inRun && part.Dock.Animating ? DockAmbientFps[_budget] : 0, idleChecks: false, out var since))
                continue;
            if (!stagger)
            {
                part.Capture.Start(now);
            }
            else if (since < nextSince)
            {
                next = part.Capture;
                nextSince = since;
            }
        }
        next?.Start(now);
    }

    private bool IsBooting()
    {
        if (_booted)
            return false;
        var mainMenu = DsGameHooks.MainMenuReady;
        if (!_inRun && !mainMenu && Time.GetTicksUsec() - _createdUsec < BootTimeoutUsec)
            return true;
        _booted = true;
        _wokeUsec = Time.GetTicksUsec();
        if (!_inRun && !mainMenu)
            DsLog.Info("no main menu after 30 s; lower screen wakes anyway");
        MarkChanged();
        return false;
    }

    private bool DeferCapture(ulong now, bool scrolling)
    {
        if (_lastFrameDtUsec <= SlowFrameUsec || TouchRecent || scrolling)
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

    // After the frame is drawn, and after this frame's readbacks came in:
    // post whatever regions changed, in one buffer.
    private void OnPostDraw()
    {
        try
        {
            if (_surfaceReady && _inRun)
            {
                var started = Time.GetTicksUsec();
                DsNative.TraceBegin("ds present");
                try
                {
                    DsNative.Present();
                }
                finally
                {
                    DsNative.TraceEnd();
                }
                DsPerf.NotePresent(Time.GetTicksUsec() - started);
            }
        }
        catch (Exception e)
        {
            DsLog.Info($"present failed, bottom screen stopped: {e.Message}");
            RenderingServer.FramePostDraw -= OnPostDraw;
        }
    }

    private void UpdateRunState()
    {
        var inRun = DsGameHooks.InRun();
        if (inRun == _inRun)
            return;
        _inRun = inRun;
        _root.Visible = inRun;
        _idle.Visible = !inRun;
        foreach (var part in _parts)
            part.Capture.Enabled = inRun;
        _dock.SetLive(inRun);
        if (inRun)
            FreeIdleArt();
        ReleasePointer();
        ApplyLayout(_surfaceW, _surfaceH);
        MarkChanged();
        DsLog.Info(inRun ? "run started; lower screen shown" : "no run; lower screen shows the menu art");
        if (!inRun)
            return;
        // The game's localization is up by now.
        _dock.RefreshTitles();
        RefreshPlaceholders();
        Show(Array.IndexOf(Tabs, _config.DefaultTab) >= 0 ? _config.DefaultTab : "map");
        // The capstone backstop's fade.
        DsStyle.Kill(ref _runTween);
        _root.Modulate = _root.Modulate with { A = 0f };
        foreach (var part in _parts)
        {
            part.Root.Modulate = part.Root.Modulate with { A = 0f };
            var fade = DsStyle.Animate(part.Root, 0.5);
            fade.TweenProperty(part.Root, "modulate:a", 1f, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        }
        _runTween = DsStyle.Animate(_root, 0.5).SetParallel(true);
        _runTween.TweenProperty(_root, "modulate:a", 1f, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        DsGameHooks.OnSurfaceReady();
    }

    private void ReleasePointer()
    {
        CancelPress();
        _penPending = false;
        StopScroll();
        _pointerId = -1;
        _fingers.Clear();
        _twoFinger = false;
    }

    // A hitch is a game frame that took more than 25 ms (a dropped frame at
    // 60 fps). The budget counts only frames well over the game's own pace,
    // so a game running at 30 fps is not one long hitch.
    private void TrackFramePacing(ulong now)
    {
        if (_lastFrameUsec != 0)
        {
            var dt = now - _lastFrameUsec;
            DsPerf.EndFrame(now, dt, _active, DsGameHooks.InCombat);
            _lastFrameDtUsec = dt;
            _frameUsecTotal += dt;
            _frameUsecMax = Math.Max(_frameUsecMax, dt);
            _frameCount++;
            if (dt > 25_000)
                _hitches++;
            _budgetFrames++;
            if (_frameUsecAverage > 0 && dt > Math.Max(25_000, _frameUsecAverage * 1.6))
                _budgetHitches++;
            // Long stalls (loading, the app in the background) don't set the pace.
            if (dt < 250_000)
                _frameUsecAverage = _frameUsecAverage == 0 ? dt : _frameUsecAverage * 0.97 + dt * 0.03;
        }
        _lastFrameUsec = now;
    }

    // Steps the ambient rates down while more than 5% of game frames drop
    // with something looping, and back up after a clean stretch.
    private void UpdateBudget(ulong now, bool ambient)
    {
        if (_budgetStartUsec == 0)
        {
            _budgetStartUsec = now;
            return;
        }
        var window = now - _budgetStartUsec;
        if (window < BudgetWindowUsec)
            return;
        var ratio = _budgetFrames > 0 ? (double)_budgetHitches / _budgetFrames : 0;
        if (ambient && ratio > 0.05 && _budget < PageAmbientFps.Length - 1)
        {
            _budget++;
            _budgetCleanUsec = 0;
            DsLog.Info($"game dropping frames ({ratio:P0}); ambient capture down to {PageAmbientFps[_budget]}/{DockAmbientFps[_budget]} fps");
        }
        else if (ratio < 0.01)
        {
            _budgetCleanUsec += window;
            if (_budgetCleanUsec >= BudgetRecoverUsec && _budget > 0)
            {
                _budget--;
                _budgetCleanUsec = 0;
                DsLog.Info($"game frames clean; ambient capture up to {PageAmbientFps[_budget]}/{DockAmbientFps[_budget]} fps");
            }
        }
        else
        {
            _budgetCleanUsec = 0;
        }
        _budgetStartUsec = now;
        _budgetFrames = _budgetHitches = 0;
    }

    private void LogStats()
    {
        var stats = DsNative.Stats();
        var frame = _frameCount > 0 ? _frameUsecTotal / 1000.0 / _frameCount : 0;
        var gcMinor = GC.CollectionCount(0);
        var gcMajor = GC.CollectionCount(GC.MaxGeneration);
        DsLog.Info($"frames posted={stats.Posted} unchanged={stats.Skipped} touch_overflows={stats.Overflows} tab={_active} combat={DsGameHooks.InCombat} deferred={_deferred} ambient={PageAmbientFps[_budget]}/{DockAmbientFps[_budget]}"
            + $" | {_page.TakeStats()} | {string.Join(" | ", Array.ConvertAll(_parts, part => part.Capture.TakeStats()))}"
            + $" | game frame_avg={frame:F1}ms frame_max={_frameUsecMax / 1000.0:F1}ms hitches={_hitches}/{_frameCount} gc_minor=+{gcMinor - _gcMinor} gc_major=+{gcMajor - _gcMajor}"
            + $" | {DsPerf.TakeStats()}");
        _frameUsecTotal = _frameUsecMax = 0;
        _frameCount = _hitches = _deferred = 0;
        _gcMinor = gcMinor;
        _gcMajor = gcMajor;
    }

    // ── touch ──────────────────────────────────────────────────────────────

    // The corner plates lie over the pages, so they are asked first.
    private DockPart PartAt(DsNative.Touch t)
    {
        if (!_inRun)
            return null;
        var at = new Vector2I((int)(t.X * _surfaceW), (int)(t.Y * _surfaceH));
        foreach (var part in _parts)
        {
            if (part.Px.HasPoint(at))
                return part;
        }
        return null;
    }

    // Canvas coordinates, pushed as local: PushInput would map Position
    // through the stretch but leave GlobalPosition in surface pixels, and the
    // map draws lines at GlobalPosition.
    private Vector2 PagePos(DsNative.Touch t)
    {
        var canvas = _viewport.Size2DOverride;
        return new Vector2(t.X * canvas.X, t.Y * _surfaceH / Math.Max(1, _pagePx) * canvas.Y);
    }

    private Vector2 PartPos(DockPart part, DsNative.Touch t)
    {
        var canvas = part.Viewport.Size2DOverride;
        return new Vector2(
            (t.X * _surfaceW - part.Px.Position.X) / Math.Max(1, part.Px.Size.X) * canvas.X,
            (t.Y * _surfaceH - part.Px.Position.Y) / Math.Max(1, part.Px.Size.Y) * canvas.Y);
    }

    private Vector2 PointerPos(DsNative.Touch t) => _pointerPart != null ? PartPos(_pointerPart, t) : PagePos(t);

    private unsafe void PumpTouches(ulong now)
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
            _lastTouchUsec = now;
            switch (t.Phase)
            {
                case DsNative.PhaseDown:
                    if (_fingers.Count == 0 && !_twoFinger)
                    {
                        // A new gesture: finish the last one's scroll first.
                        FlushScroll(now);
                        _fingers[t.Id] = PagePos(t);
                        _pointerPart = PartAt(t);
                        var pos = PointerPos(t);
                        _pointerId = t.Id;
                        _pointerPos = _dragOrigin = pos;
                        _scrollDrag = false;
                        _gestureDead = false;
                        _fling = 0;
                        _pushedPos = pos;
                        if (_pointerPart == null && DsGameHooks.IsDrawing(_active))
                        {
                            _penPending = true;
                            _penDownUsec = now;
                            break;
                        }
                        PushPointer(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                        PushPointer(MouseButtonEvent(pos, pressed: true));
                    }
                    else
                    {
                        _fingers[t.Id] = PagePos(t);
                        if (_fingers.Count == 2 && !_twoFinger && _pointerPart == null)
                        {
                            // A second finger turns the gesture into a scroll:
                            // let go of the first finger's press (without a tap)
                            // and follow the midpoint from here.
                            CancelPress();
                            _scrollDrag = false;
                            _pointerId = -1;
                            _twoFinger = true;
                            _scrollMid = Midpoint();
                            HoldScroll();
                        }
                    }
                    break;
                case DsNative.PhaseMove:
                    if (!_fingers.ContainsKey(t.Id))
                        break;
                    _fingers[t.Id] = PagePos(t);
                    if (_twoFinger)
                    {
                        if (_fingers.Count >= 2)
                        {
                            var mid = Midpoint();
                            _pendingDy += mid.Y - _scrollMid.Y;
                            _scrollMid = mid;
                        }
                        break;
                    }
                    if (t.Id != _pointerId || _gestureDead)
                        break;
                    var at = PointerPos(t);
                    var moved = at - _pointerPos;
                    _pointerPos = at;
                    if (_penPending)
                    {
                        if ((at - _dragOrigin).Length() >= PenSlop)
                            PenDown(at);
                        break;
                    }
                    if (_pointerPart == null)
                    {
                        if (_scrollDrag)
                        {
                            _pendingDy += moved.Y;
                            break;
                        }
                        if (DsGameHooks.CanScroll(_active))
                        {
                            // Held back until it is clearly a tap or a scroll.
                            if ((at - _dragOrigin).Length() < DragSlop)
                                break;
                            if (StartScrollDrag())
                                _pendingDy += at.Y - _dragOrigin.Y;
                            else
                                _gestureDead = true;
                            break;
                        }
                    }
                    PushPointer(new InputEventMouseMotion
                    {
                        Position = at,
                        GlobalPosition = at,
                        Relative = at - _pushedPos,
                        ButtonMask = MouseButtonMask.Left,
                    });
                    _pushedPos = at;
                    break;
                case DsNative.PhaseUp:
                case DsNative.PhaseCancel:
                    _fingers.Remove(t.Id);
                    if (t.Id == _pointerId)
                    {
                        // Taken away by the system: not a tap, not a dot.
                        var cancelled = t.Phase == DsNative.PhaseCancel;
                        if (cancelled)
                            CancelPress();
                        // A tap with the pen: a dot, as a click makes.
                        else if (_penPending)
                            PenDown(_dragOrigin);
                        if (_scrollDrag)
                        {
                            _releaseDue = true;
                        }
                        else if (!_gestureDead && !cancelled)
                        {
                            var up = PointerPos(t);
                            if (up != _pushedPos)
                                PushPointer(new InputEventMouseMotion { Position = up, GlobalPosition = up, Relative = up - _pushedPos, ButtonMask = MouseButtonMask.Left });
                            PushPointer(MouseButtonEvent(up, pressed: false));
                        }
                        _pointerId = -1;
                        _gestureDead = false;
                    }
                    // The rest of a two-finger gesture never becomes a press.
                    if (_fingers.Count == 0)
                    {
                        if (_twoFinger)
                            _releaseDue = true;
                        _twoFinger = false;
                    }
                    else if (_twoFinger)
                    {
                        _scrollMid = Midpoint();
                    }
                    break;
            }
        }
        // Held still long enough: the pen goes down where the finger is.
        if (_penPending && _pointerId >= 0 && now - _penDownUsec >= PenDelayUsec)
            PenDown(_pointerPos);
        FlushScroll(now);
    }

    // The press held back for drawing, at the first touch, then the line
    // drawn on to where the finger is now.
    private void PenDown(Vector2 at)
    {
        _penPending = false;
        PushPointer(new InputEventMouseMotion { Position = _dragOrigin, GlobalPosition = _dragOrigin });
        PushPointer(MouseButtonEvent(_dragOrigin, pressed: true));
        _pushedPos = _dragOrigin;
        if (at == _dragOrigin)
            return;
        PushPointer(new InputEventMouseMotion
        {
            Position = at,
            GlobalPosition = at,
            Relative = at - _dragOrigin,
            ButtonMask = MouseButtonMask.Left,
        });
        _pushedPos = at;
    }

    // Lets go of the touch's press without it counting as a tap: off screen,
    // or, mid-line, where it is (moving it away would draw a line there).
    private void CancelPress()
    {
        if (_pointerId < 0 || _scrollDrag || _gestureDead)
            return;
        if (_penPending)
        {
            _penPending = false;
            return;
        }
        if (_pointerPart == null && DsGameHooks.IsDrawing(_active))
            PushPointer(MouseButtonEvent(_pushedPos, pressed: false));
        else
            ReleaseAway();
    }

    // Motion, then the release, far off the canvas. No relative movement,
    // so nothing dragging (the map's own drag) moves with it.
    private void ReleaseAway()
    {
        var away = new Vector2(-10000f, -10000f);
        PushPointer(new InputEventMouseMotion { Position = away, GlobalPosition = away });
        PushPointer(MouseButtonEvent(away, pressed: false));
        _pushedPos = away;
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

    // Past the slop, the touch lets go of whatever it pressed (off screen,
    // so nothing counts it as a tap) and moves the map or pile instead.
    private bool StartScrollDrag()
    {
        ReleaseAway();
        if (!HoldScroll())
            return false;
        _scrollDrag = true;
        return true;
    }

    private bool HoldScroll()
    {
        if (!_heldScroll)
            _heldScroll = DsGameHooks.HoldScroll(_active, true);
        _velocityPrimed = false;
        _scrollVelocity = 0;
        _pendingDy = 0;
        return _heldScroll;
    }

    // Everything the fingers moved this frame, applied at once: one layout
    // per frame, and a speed measured between frames rather than between
    // touch events that arrived together.
    private void FlushScroll(ulong now)
    {
        if (_pendingDy != 0 && _heldScroll)
        {
            DsGameHooks.ScrollBy(_active, _pendingDy);
            var gap = now - _lastScrollUsec;
            if (_velocityPrimed && gap > 0 && gap < 100_000)
            {
                var velocity = _pendingDy / (gap / 1e6f);
                _scrollVelocity = _scrollVelocity == 0 ? velocity : Mathf.Lerp(_scrollVelocity, velocity, 0.5f);
            }
            else
            {
                _scrollVelocity = 0;
            }
            _velocityPrimed = true;
            _lastScrollUsec = now;
            _scrollingUntilUsec = now + ScrollTailUsec;
            MarkChanged();
        }
        _pendingDy = 0;
        if (!_releaseDue)
            return;
        _releaseDue = false;
        _scrollDrag = false;
        if (_heldScroll)
            DsGameHooks.HoldScroll(_active, false);
        _heldScroll = false;
        // A finger that stopped before lifting doesn't fling.
        var still = now - _lastScrollUsec > 80_000;
        _fling = still || Math.Abs(_scrollVelocity) < FlingMin * 4 ? 0 : _scrollVelocity;
        _lastFlingUsec = now;
        _scrollingUntilUsec = now + ScrollTailUsec;
    }

    private void StopScroll()
    {
        if (_heldScroll && _active != null)
            DsGameHooks.HoldScroll(_active, false);
        _heldScroll = false;
        _scrollDrag = false;
        _releaseDue = false;
        _pendingDy = 0;
        _fling = 0;
    }

    private void UpdateFling(ulong now)
    {
        if (_fling == 0)
            return;
        var dt = (now - _lastFlingUsec) / 1e6f;
        _lastFlingUsec = now;
        // Stops at an end, where the map's or grid's own spring takes over.
        if (_pointerId >= 0 || _twoFinger || !DsGameHooks.ScrollBy(_active, _fling * dt))
        {
            _fling = 0;
            return;
        }
        _fling *= MathF.Exp(-FlingDecay * dt);
        if (Math.Abs(_fling) < FlingMin)
            _fling = 0;
        _scrollingUntilUsec = now + ScrollTailUsec;
        MarkChanged();
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

    private void Push(InputEvent e) => PushAsTouch(_viewport, e);

    private void PushPointer(InputEvent e) => PushAsTouch(_pointerPart?.Viewport ?? _viewport, e);

    // The game's nodes down here read its one input mode; a finger is a
    // finger whatever the controller up top is doing.
    private static void PushAsTouch(SubViewport viewport, InputEvent e)
    {
        var restore = DsGameHooks.BeginTouchDispatch();
        try
        {
            viewport.PushInput(e, true);
        }
        finally
        {
            DsGameHooks.EndTouchDispatch(restore);
        }
    }
}
