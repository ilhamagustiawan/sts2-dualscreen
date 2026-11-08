using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using Expr = System.Linq.Expressions.Expression;

namespace STS2DualScreen;

// Where the bottom screen meets the game.
//
// The bottom screen is a companion: the top screen is the game as it is,
// and the bottom one shows its own copies of the game's screens, for touch.
// Nothing down here opens, closes or replaces anything up top.
//
// Map: a second NMapScreen from the game's own scene, in the bottom
// screen's Map page. The game only ever reaches its map through
// NMapScreen.Instance (the one in its global UI), so it never calls this
// one; the hooks below mirror the top map's state into it (run state, act
// map, marker, travel state, rebuilt after each room) and keep it from
// touching the game (cursor, top bar, travel). It scrolls, shows room tips
// and draws; travel is the top map's. Drawings are shared: a line on
// either map is mirrored onto the other, and only the top one talks to the
// network, so lines drawn below are saved and sent as if drawn up top.
//
// Piles: each pile tab hosts a fresh instance of the game's NCardPileScreen
// bound to the live CardPile, so it updates as cards move. The top screen's
// pile buttons and hotkeys open the game's own overlays as usual.
//
// Input mode: the bottom screen is for touch. The few changes to the top's
// input handling (the mode toast, stray mouse events, the compat pack's
// touch-only flows on a controller, window focus) are each explained where
// they are made.
//
// All game types are looked up by name. If one is missing (a game update),
// that feature logs and stays off; nothing here throws into the game.
internal static class DsGameHooks
{
    private const string MapScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen";
    private const string CardPileScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen";
    private const string CombatCardPileType = "MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile";
    private const string TopBarDeckButtonType = "MegaCrit.sts2.Core.Nodes.TopBar.NTopBarDeckButton";
    private const string PileTypeType = "MegaCrit.Sts2.Core.Entities.Cards.PileType";
    private const string PileTypeExtensionsType = "MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions";
    private const string MapPointType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint";
    private const string NormalMapPointType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint";
    private const string MapDrawingInputType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapDrawingInput";
    private const string MapDrawingsType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapDrawings";
    private const string RunStateType = "MegaCrit.Sts2.Core.Runs.RunState";
    private const string MapScenePath = "res://scenes/screens/map/map_screen.tscn";
    private const string ControllerManagerType = "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager";
    private const string ActiveScreenContextType = "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext";
    private const string MouseModeMapDrawingInputType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMouseModeMapDrawingInput";
    private const string PlayerHandType = "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand";
    private const string CardRewardScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    private const string ChooseCardScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen";
    private const string CompatMerchantConfirmType = "STS2Mobile.Patches.MerchantSelectionConfirmationPatches";
    private const string GameType = "MegaCrit.Sts2.Core.Nodes.NGame";
    private const string RunType = "MegaCrit.Sts2.Core.Nodes.NRun";
    private const string PlatformUtilType = "MegaCrit.Sts2.Core.Platform.PlatformUtil";
    private const string HoverTipSetType = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";
    private const string MainMenuType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string CardHolderType = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder";

    // The game's UI is sized for 1080p; on the small lower panel its cards
    // and map tools read small. These scale them up there only.
    private const float PileScale = 1.35f;
    private const float MapToolsScale = 1.4f;
    private const float LegendMargin = 8f;

    private static readonly Dictionary<string, string> TabToPileType = new()
    {
        ["draw"] = "Draw",
        ["discard"] = "Discard",
        ["exhaust"] = "Exhaust",
        ["deck"] = "Deck",
    };

    private static Type _mapType;
    private static Type _pileScreenType;
    private static MethodInfo _getPile;
    private static Type _pileTypeEnum;
    private static Type _controllerManagerType;
    private static PropertyInfo _managerInstance, _gameInstance;
    private static FieldInfo _skipMouseFrames, _lastMousePosition;
    private static MethodInfo _gameFocused;
    private static GodotObject _seenManager;
    private static Type _screenContextType;
    private static MethodInfo _tapPreviewPrefix;
    private static MethodInfo _rewardSelectPrefix;
    private static MethodInfo _chooseSelectPrefix;
    private static PropertyInfo _runInstance;
    private static Func<object, object> _runInstanceGet;
    private static MethodInfo _overlayOpen;
    private static Func<object, bool> _overlayOpenCall;
    private static PropertyInfo _contextInstance;
    private static MethodInfo _contextFocusDefault;

    // NMapScreen members, resolved once: some are read on every scrolled frame.
    private static FieldInfo _mapContainer, _mapTargetDrag, _mapDragging, _mapDrawingInput, _mapTween;
    private static FieldInfo _mapBackstop, _mapLegend, _mapTools, _mapPoints, _mapRunState, _mapDistY;
    private static FieldInfo _mapActMap, _mapBackButton, _mapLegendHotkey, _mapToolsHotkey;
    private static PropertyInfo _mapTravelEnabled, _mapDebugTravelEnabled, _mapDrawings;
    private static Func<object, bool> _mapTravelGet;
    private static MethodInfo _mapCanScroll, _mapCancelActAnim;
    private static MethodInfo _mapInitialize, _mapSetMap, _mapInitMarker, _mapRefreshVotes, _mapRefreshVisuals;
    private static float _mapScrollLow = -600f, _mapScrollHigh = 1800f;
    private static bool _focusOverridden;

    // The game's map, on the top screen, and the bottom screen's copy of it.
    private static Control _topMap;
    private static Control _map;
    // Boxed as SetMap takes it: its type has changed between versions (uint, ulong).
    private static object _mapSeed;
    private static object _markerCoord;
    private static bool _rebuildQueued;
    private static object _player;
    private static bool _inCombat;

    /// <summary>A fight is on: its card piles exist.</summary>
    public static bool InCombat => _inCombat;

    /// <summary>The tab a fight opens on.</summary>
    public const string CombatStartTab = "draw";

    /// <summary>The game has finished booting to its main menu at least once.</summary>
    public static bool MainMenuReady { get; private set; }
    private static readonly Dictionary<string, Node> PileScreens = new();
    private static readonly Dictionary<string, object> PileBindings = new();
    private static readonly HashSet<string> ResizeHooked = new();

    /// <summary>
    /// By full name, else by simple name in the game assembly: namespaces
    /// have moved between game versions (TopBar was MegaCrit.sts2.* in one).
    /// </summary>
    private static Type FindType(string fullName)
    {
        var type = AccessTools.TypeByName(fullName);
        if (type != null)
            return type;
        var simple = fullName.Substring(fullName.LastIndexOf('.') + 1);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "sts2")
                continue;
            try
            {
                foreach (var candidate in assembly.GetTypes())
                {
                    if (candidate.Name == simple)
                        return candidate;
                }
            }
            catch (ReflectionTypeLoadException e)
            {
                foreach (var candidate in e.Types)
                {
                    if (candidate?.Name == simple)
                        return candidate;
                }
            }
        }
        return null;
    }

    public static void Apply(Harmony harmony)
    {
        _mapType = FindType(MapScreenType);
        ResolveMap();
        _pileScreenType = FindType(CardPileScreenType);
        _pileTypeEnum = FindType(PileTypeType);
        var extensions = FindType(PileTypeExtensionsType);
        _getPile = extensions == null ? null : AccessTools.Method(extensions, "GetPile");
        ResolvePileScreen();

        Patch(harmony, _mapType, "_Ready", postfix: nameof(MapReadyPostfix));
        Patch(harmony, _mapType, "Initialize", postfix: nameof(MapInitializePostfix));
        Patch(harmony, _mapType, "SetMap", postfix: nameof(MapSetMapPostfix));
        Patch(harmony, _mapType, "InitMarker", postfix: nameof(MapInitMarkerPostfix));
        Patch(harmony, _mapType, "RefreshAllMapPointVotes", postfix: nameof(MapRefreshVotesPostfix));
        Patch(harmony, _mapType, "SetTravelEnabled", postfix: nameof(MapTravelPostfix));
        Patch(harmony, _mapType, "SetDebugTravelEnabled", postfix: nameof(MapTravelPostfix));
        // The copy's own reactions that reach past it: taking the network
        // cursor from the top map, rocking the top bar's map button, the
        // hotkey icons (a controller never drives it), and a travel vote.
        Patch(harmony, _mapType, "OnVisibilityChanged", prefix: nameof(TopMapOnlyPrefix));
        Patch(harmony, _mapType, "OnCapstoneChanged", prefix: nameof(TopMapOnlyPrefix));
        Patch(harmony, _mapType, "UpdateHotkeyDisplay", prefix: nameof(TopMapOnlyPrefix));
        Patch(harmony, _mapType, "OnMapPointSelectedLocally", prefix: nameof(TopMapOnlyPrefix));
        Patch(harmony, _mapType, "get_MapLegendX", postfix: nameof(MapLegendXPostfix));
        Patch(harmony, FindType(MapPointType), "OnRelease", prefix: nameof(MapPointReleasePrefix));
        Patch(harmony, FindType(NormalMapPointType), "_ExitTree", finalizer: nameof(CopyPointExitFinalizer));
        Patch(harmony, FindType(RunStateType), "AddVisitedMapCoord", postfix: nameof(VisitedMapCoordPostfix));
        Patch(harmony, FindType(MapDrawingInputType), "Create", prefix: nameof(DrawingInputCreatePrefix), postfix: nameof(DrawingInputCreatePostfix));
        ApplyDrawingHooks(harmony);
        _controllerManagerType = FindType(ControllerManagerType);
        ResolveControllerManager();
        _screenContextType = FindType(ActiveScreenContextType);
        if (_screenContextType != null)
        {
            _contextInstance = AccessTools.Property(_screenContextType, "Instance");
            _contextFocusDefault = AccessTools.Method(_screenContextType, "FocusOnDefaultControl");
        }
        // The "Controller detected" / "Mouse detected" toast; the mode switch
        // itself happens before this and is untouched.
        Patch(harmony, _controllerManagerType, "ControlModeChanged", prefix: nameof(SkipPrefix));
        Patch(harmony, _controllerManagerType, "CheckForMouseInput", prefix: nameof(MouseCheckPrefix));
        Patch(harmony, _controllerManagerType, "CheckForControllerInput", prefix: nameof(ControllerCheckPrefix));
        Patch(harmony, FindType(MouseModeMapDrawingInputType), "_Input", prefix: nameof(NotQueuedForDeletionPrefix));
        GuardTouchOnlyPatches(harmony);
        // Touching the lower screen takes window focus from the game, and the
        // game drops every hotkey and controller action while unfocused.
        Patch(harmony, FindType(GameType), "IsGameFocusedWindow", postfix: nameof(GameFocusedPostfix));
        _overlayOpen = FindType(PlatformUtilType) is { } platform ? AccessTools.Method(platform, "IsPlatformOverlayOpen") : null;
        _overlayOpenCall = Calling<bool>(_overlayOpen);
        _runInstance = FindType(RunType) is { } run ? AccessTools.Property(run, "Instance") : null;
        _runInstanceGet = Getter<object>(_runInstance);
        var hoverTips = FindType(HoverTipSetType);
        // The single-tip overload calls this one.
        var createAndShow = hoverTips == null ? null : AccessTools.GetDeclaredMethods(hoverTips)
            .FirstOrDefault(m => m.Name == "CreateAndShow" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType.IsGenericType);
        Patch(harmony, hoverTips, createAndShow, "CreateAndShow", postfix: nameof(HoverTipPostfix));
        Patch(harmony, hoverTips, "CreateAndShowMapPointHistory", postfix: nameof(HoverTipPostfix));
        Patch(harmony, FindType(CombatCardPileType), "Initialize", postfix: nameof(CombatPileInitializePostfix));
        Patch(harmony, FindType(CombatCardPileType), "AddCard", postfix: nameof(CombatPileCountPostfix));
        Patch(harmony, FindType(CombatCardPileType), "RemoveCard", postfix: nameof(CombatPileCountPostfix));
        Patch(harmony, FindType(CombatCardPileType), "_ExitTree", postfix: nameof(CombatPileExitPostfix));
        Patch(harmony, FindType(TopBarDeckButtonType), "Initialize", postfix: nameof(DeckButtonInitializePostfix));
        Patch(harmony, FindType(TopBarDeckButtonType), "OnPileContentsChanged", postfix: nameof(DeckCountPostfix));
        Patch(harmony, _pileScreenType, "OnPileContentsChanged", prefix: nameof(PileContentsChangedPrefix));
        var cardHolder = FindType(CardHolderType);
        if (cardHolder != null)
        {
            _holderHoverTween = AccessTools.Field(cardHolder, "_hoverTween");
            _holderHoverScale = AccessTools.Property(cardHolder, "HoverScale");
            _holderSmallScale = AccessTools.Property(cardHolder, "SmallScale");
        }
        Patch(harmony, cardHolder, "DoCardHoverEffects", postfix: nameof(CardHoverPostfix));
        Patch(harmony, FindType(MainMenuType), "_Ready", postfix: nameof(MainMenuReadyPostfix));
    }

    private static void MainMenuReadyPostfix()
    {
        if (MainMenuReady)
            return;
        MainMenuReady = true;
        DsLog.Info("main menu ready; lower screen wakes");
        // The game starts in mouse mode until a pad is pressed; with a pad in
        // hand (the Thor always has one) the menu starts focused instead.
        Input.JoyConnectionChanged += (_, connected) =>
        {
            if (connected && !IsUsingController())
                EnterControllerMode("controller connected");
        };
        Callable.From(() =>
        {
            if (Input.GetConnectedJoypads().Count > 0 && !IsUsingController() && _lastPointerPressUsec == 0)
                EnterControllerMode("controller connected at boot");
        }).CallDeferred();
    }

    private static void Patch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
    {
        MethodInfo target = null;
        try
        {
            target = type == null ? null : AccessTools.Method(type, method);
        }
        catch (AmbiguousMatchException)
        {
        }
        Patch(harmony, type, target, method, prefix, postfix, finalizer);
    }

    private static void Patch(Harmony harmony, Type type, MethodInfo target, string method, string prefix = null, string postfix = null, string finalizer = null)
    {
        if (type == null)
        {
            DsLog.Warn($"hook skipped, type not found for {method} ({prefix ?? postfix ?? finalizer})");
            return;
        }
        try
        {
            if (target == null)
            {
                DsLog.Warn($"hook skipped, {type.Name}.{method} not found");
                return;
            }
            harmony.Patch(
                target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(DsGameHooks), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(DsGameHooks), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(DsGameHooks), finalizer)
            );
        }
        catch (Exception e)
        {
            DsLog.Warn($"hook {type.Name}.{method} failed: {e.Message}");
        }
    }

    // ── bottom screen lifecycle ────────────────────────────────────────────

    public static void OnBottomScreenReady()
    {
        SetCombatTabs(_inCombat);
        QueueRebuild();
    }

    public static void OnSurfaceReady()
    {
        if (MapValid())
            RevealMap(_map, scrollToCurrent: false);
    }

    public static void OnTabShown(string tab)
    {
        if (TabToPileType.ContainsKey(tab))
            EnsurePileScreen(tab);
        else if (tab == "map")
            UpdateMapPlaceholder();
    }

    /// <summary>
    /// A pile tab was left: its cards' motion ends now. Paused with its page
    /// (pause_hidden_pages), the tween would stay running, so the pile would
    /// stay pending (CardsMoving) and mid-exit (ExitingPiles) until shown
    /// again, then replay a stale motion. Cards leaving finish leaving: the
    /// pile is stale, and its tab rebuilds it when opened.
    /// </summary>
    public static void OnTabHidden(string tab)
    {
        if (TabToPileType.ContainsKey(tab))
            SettleCards(tab);
    }

    /// <summary>While travel is on, the map's rooms you can reach pulse forever (NNormalMapPoint).</summary>
    public static bool MapAmbient() => MapValid() && _mapTravelGet?.Invoke(_map) == true;

    /// <summary>A run is loaded (NRun exists). True when that can't be told, so nothing goes missing.</summary>
    public static bool InRun()
    {
        if (_runInstanceGet == null)
            return true;
        try
        {
            return _runInstanceGet(null) != null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static object ScreenContext() => _contextInstance?.GetValue(null);

    // The game counts as focused while it is on screen with the lower screen
    // up (that is, between the activity's onStart and onStop), even if a touch
    // down there moved window focus.
    private static void GameFocusedPostfix(ref bool __result)
    {
        if (__result)
        {
            if (_focusOverridden)
            {
                _focusOverridden = false;
                DsLog.Debug("game window focused again");
            }
            return;
        }
        if (DsBottomScreen.Instance?.Ready != true)
            return;
        try
        {
            if (_overlayOpenCall?.Invoke(null) == true)
                return;
        }
        catch (Exception)
        {
        }
        __result = true;
        if (!_focusOverridden)
        {
            _focusOverridden = true;
            DsLog.Debug("game window lost focus to the lower screen; still taking input");
        }
    }

    // The game's input mode. Up to v0.107 a flag, IsUsingController; since
    // v0.111 an enum, InputType (MouseAndKeyboard, Controller,
    // KeyboardOnlyMode), and controller mode also turns the mouse off for
    // the whole game node (MouseBehaviorRecursive). Held as the value the
    // game uses for mouse mode and for controller mode, whichever it is.
    private static PropertyInfo _mode, _directional;
    private static FieldInfo _modeField;
    private static object _mouseMode, _controllerMode;
    private static bool _modeIsEnum;

    private static void ResolveControllerManager()
    {
        if (_controllerManagerType == null)
            return;
        _managerInstance = AccessTools.Property(_controllerManagerType, "Instance");
        _mode = AccessTools.Property(_controllerManagerType, "InputType");
        if (_mode?.PropertyType.IsEnum == true)
        {
            _modeIsEnum = true;
            _mouseMode = Enum.TryParse(_mode.PropertyType, "MouseAndKeyboard", out var mouse) ? mouse : null;
            _controllerMode = Enum.TryParse(_mode.PropertyType, "Controller", out var pad) ? pad : null;
            _directional = AccessTools.Property(_controllerManagerType, "IsUsingDirectionalNavigation");
        }
        else
        {
            _mode = AccessTools.Property(_controllerManagerType, "IsUsingController");
            _mouseMode = false;
            _controllerMode = true;
        }
        // Without a setter, the compiler's backing field still holds it.
        _modeField = _mode == null || _mode.CanWrite ? null : AccessTools.Field(_controllerManagerType, $"<{_mode.Name}>k__BackingField");
        // Both only steady the mouse around the switch; it works without them.
        _skipMouseFrames = AccessTools.Field(_controllerManagerType, "_skipMouseCheckFrames");
        _lastMousePosition = AccessTools.Field(_controllerManagerType, "_lastMousePosition");
        var game = FindType(GameType);
        _gameFocused = game == null ? null : AccessTools.Method(game, "IsGameFocusedWindow");
        _gameInstance = game == null ? null : AccessTools.Property(game, "Instance");
        var missing = new List<string>();
        if (_managerInstance == null) missing.Add("Instance");
        if (_mode?.CanRead != true) missing.Add("InputType or IsUsingController");
        else if (!CanSwitchModes()) missing.Add($"a way to set {_mode.Name}");
        if (_gameFocused == null) missing.Add("NGame.IsGameFocusedWindow");
        if (missing.Count > 0)
            DsLog.Warn($"controller manager has no {string.Join(", ", missing)}"
                + (CanSwitchModes() ? "" : "; the game switches input modes its own way"));
        else
            DsLog.Info($"input mode: {_mode.Name} ({_mouseMode} / {_controllerMode})");
    }

    private static bool CanSwitchModes() =>
        _mode?.CanRead == true && (_mode.CanWrite || _modeField != null) && _mouseMode != null && _controllerMode != null;

    private static object GetMode(object manager) => _mode.GetValue(manager);

    private static void SetMode(object manager, object value)
    {
        if (_mode.CanWrite)
            _mode.SetValue(manager, value);
        else
            _modeField.SetValue(manager, value);
    }

    // Instance, or else the one whose input checks last ran.
    private static object ControllerManager() =>
        _managerInstance?.GetValue(null) ?? (GodotObject.IsInstanceValid(_seenManager) ? _seenManager : null);

    private static bool SkipPrefix() => false;

    // A stopped drawing tool is freed at the end of the frame; touches later
    // in that frame would start a line with no drawing mode and throw.
    private static bool NotQueuedForDeletionPrefix(object __instance) =>
        __instance is not Node node || !node.IsQueuedForDeletion();

    /// <summary>The game is in controller (or keyboard-only) mode: focus moves, not the mouse.</summary>
    private static bool IsUsingController()
    {
        if (ControllerManager() is not { } manager || _mode?.CanRead != true)
            return false;
        if (_directional?.GetValue(manager) is bool directional)
            return directional;
        return Equals(GetMode(manager), _controllerMode);
    }

    // ── input mode ─────────────────────────────────────────────────────────

    // The top screen is for the controller. The game leaves controller mode
    // on any mouse event at all: a release, the wheel, or motion, and on the
    // Thor such events turn up with no mouse in hand. Only a tap (Godot's
    // click for it) or a real mouse's click switches to touch mode here.
    private static int _mouseIgnoredLogged;

    private static bool MouseCheckPrefix(GodotObject __instance, InputEvent inputEvent)
    {
        _seenManager = __instance;
        if (inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle })
            return true;
        // The first few the game would have acted on, to tell where they come from.
        var counted = inputEvent is InputEventMouseButton || inputEvent is InputEventMouseMotion motion && motion.Velocity.LengthSquared() > 100f;
        if (counted && _mouseIgnoredLogged++ < 5 && inputEvent is InputEventMouse mouse)
            DsLog.Debug($"ignored {mouse.GetType().Name} for input mode: device={mouse.Device} at {mouse.Position}");
        return false;
    }

    // Back from touch, the game's first controller press only switches mode
    // and focuses the screen's default control; it is swallowed. A direction
    // moves focus at once instead. Other buttons keep the game's way, so A
    // never presses a control that was not visibly focused yet.
    private static readonly string[] DirectionActionNames =
    {
        "controller_d_pad_north", "controller_d_pad_south", "controller_d_pad_east", "controller_d_pad_west",
        "controller_joystick_up", "controller_joystick_down", "controller_joystick_left", "controller_joystick_right",
    };
    private static StringName[] _directionActions;

    private static bool IsDirectionPress(InputEvent e)
    {
        // Asking about an action the input map lacks logs an error each time.
        _directionActions ??= DirectionActionNames.Select(name => new StringName(name)).Where(InputMap.HasAction).ToArray();
        foreach (var action in _directionActions)
        {
            if (e.IsActionPressed(action))
                return true;
        }
        return false;
    }

    private static bool ControllerCheckPrefix(GodotObject __instance, InputEvent inputEvent)
    {
        _seenManager = __instance;
        if (!CanSwitchModes() || _gameFocused == null || !IsDirectionPress(inputEvent))
            return true;
        try
        {
            if (_gameFocused.Invoke(null, null) is not true)
                return true;
        }
        catch (Exception)
        {
            return true;
        }
        return !EnterControllerMode("direction pressed");
    }

    private static readonly Vector2 OffscreenMouse = Vector2.One * -1000f;
    private static readonly HashSet<string> ModeLogged = new();

    /// <summary>
    /// The game's own switch to controller mode (as CheckForControllerInput
    /// does it), but without swallowing the event that caused it.
    /// </summary>
    private static bool EnterControllerMode(string why)
    {
        if (!CanSwitchModes())
            return false;
        if (ControllerManager() is not Node manager)
        {
            if (ModeLogged.Add(why))
                DsLog.Warn($"no controller manager to switch ({why})");
            return false;
        }
        try
        {
            SetMode(manager, _controllerMode);
            if (_modeIsEnum)
            {
                // As the game does it since v0.111: the mouse off for the game's nodes.
                if (_gameInstance?.GetValue(null) is Control game)
                    game.MouseBehaviorRecursive = Control.MouseBehaviorRecursiveEnum.Disabled;
            }
            else if (manager.GetViewport() is { } viewport)
            {
                var mouse = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
                _lastMousePosition?.SetValue(manager, new Vector2(mouse.X, mouse.Y));
                viewport.WarpMouse(OffscreenMouse);
                _skipMouseFrames?.SetValue(manager, 2);
            }
            if (ScreenContext() is { } context)
                _contextFocusDefault?.Invoke(context, null);
            // The cursor, hotkey icons and the like follow this signal.
            manager.EmitSignal("ControllerDetected");
            if (ModeLogged.Add(why))
                DsLog.Info($"controller mode: {why}");
            return true;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not switch to controller mode ({why}): {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    /// <summary>
    /// A pointer event for the lower screen is about to be pushed. The lower
    /// screen is for touch: while it is handled, the game's nodes there see
    /// mouse mode, whatever the controller up top is doing. Only the mode
    /// value changes (no signals, no focus change, nothing up top), and only
    /// for that one push. Pass the result to EndTouchDispatch.
    /// </summary>
    public static object BeginTouchDispatch()
    {
        if (!CanSwitchModes() || ControllerManager() is not { } manager)
            return null;
        var mode = GetMode(manager);
        if (Equals(mode, _mouseMode))
            return null;
        SetMode(manager, _mouseMode);
        return mode;
    }

    public static void EndTouchDispatch(object previous)
    {
        if (previous != null && ControllerManager() is { } manager)
            SetMode(manager, previous);
    }

    // Whether the press being handled came from a controller. The game's mode
    // no longer drops to touch on stray mouse events (MouseCheckPrefix), but a
    // press is still judged by the last press the window saw, controller (pad,
    // key, action) or pointer (touch, click), in case something else moves
    // it; the game's mode only decides before either has happened.
    private static ulong _lastPadPressUsec, _lastPointerPressUsec;

    /// <summary>Every event the top window receives, before anything handles it.</summary>
    public static void NoteInput(InputEvent e)
    {
        var now = Time.GetTicksUsec();
        switch (e)
        {
            case InputEventScreenTouch { Pressed: true }:
            case InputEventMouseButton { Pressed: true }:
                _lastPointerPressUsec = now;
                break;
            case InputEventJoypadButton { Pressed: true }:
            case InputEventAction { Pressed: true }:
            case InputEventKey { Pressed: true, Echo: false }:
            case InputEventJoypadMotion motion when Math.Abs(motion.AxisValue) > 0.5f:
                _lastPadPressUsec = now;
                break;
        }
    }

    private static bool PressFromController() =>
        _lastPadPressUsec == 0 && _lastPointerPressUsec == 0 ? IsUsingController() : _lastPadPressUsec > _lastPointerPressUsec;

    // The compat pack makes some card presses touch-friendly, and doesn't
    // check where the press came from, so on a controller A never finishes:
    // - hand: "tap to preview" turns the first press into a pinned preview
    //   and, by default, the second into putting it back down, so A never
    //   plays a card;
    // - card reward and "choose a card": a press only marks the card and
    //   enables a Confirm button, and A on the focused card just marks it
    //   again.
    // Each prefix is swapped for one that runs it only when not on a
    // controller, so A behaves as on PC and touch keeps the mobile flow.
    // The shop's version (every slot press waits for a Confirm button) has
    // one gate for all of its intercepts; that gate says no on a controller.
    private static void GuardTouchOnlyPatches(Harmony harmony)
    {
        var merchant = AccessTools.TypeByName(CompatMerchantConfirmType);
        if (merchant == null)
            DsLog.Debug("no compat shop confirmation; nothing to guard");
        else
            Patch(harmony, merchant, "ShouldConfirmSlotSelection", prefix: nameof(MerchantConfirmPrefix));
        _tapPreviewPrefix = SwapForTouchOnly(harmony, PlayerHandType, "OnHolderPressed", "OnHolderPressedPrefix", nameof(HolderPressedPrefix));
        _rewardSelectPrefix = SwapForTouchOnly(harmony, CardRewardScreenType, "SelectCard", "CardRewardSelectPrefix", nameof(RewardSelectPrefix));
        _chooseSelectPrefix = SwapForTouchOnly(harmony, ChooseCardScreenType, "SelectHolder", "ChooseCardSelectPrefix", nameof(ChooseSelectPrefix));
    }

    private static MethodInfo SwapForTouchOnly(Harmony harmony, string typeName, string method, string compatPrefix, string guard)
    {
        var type = FindType(typeName);
        var target = type == null ? null : AccessTools.Method(type, method);
        if (target == null)
            return null;
        try
        {
            var patch = Harmony.GetPatchInfo(target)?.Prefixes
                .FirstOrDefault(p => p.PatchMethod.Name == compatPrefix && p.owner != harmony.Id);
            if (patch == null)
            {
                DsLog.Debug($"no {compatPrefix} on {type.Name}.{method}; nothing to guard");
                return null;
            }
            harmony.Unpatch(target, patch.PatchMethod);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DsGameHooks), guard));
            DsLog.Debug($"{type.Name}.{method}: {compatPrefix} limited to touch ({patch.owner})");
            return patch.PatchMethod;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not guard {type.Name}.{method}: {e.Message}");
            return null;
        }
    }

    private static bool HolderPressedPrefix(object __instance, object holder) => RunUnlessController(_tapPreviewPrefix, __instance, holder);

    private static bool RewardSelectPrefix(object __instance, object cardHolder) => RunUnlessController(_rewardSelectPrefix, __instance, cardHolder);

    private static bool ChooseSelectPrefix(object __instance, object cardHolder) => RunUnlessController(_chooseSelectPrefix, __instance, cardHolder);

    private static bool MerchantConfirmPrefix(ref bool __result)
    {
        if (!PressFromController())
            return true;
        __result = false;
        return false;
    }

    // The first presses of each kind are logged, to tell a press that never
    // arrives from one the compat pack or the game turns away.
    private static readonly Dictionary<string, int> PressesLogged = new();

    private static bool RunUnlessController(MethodInfo compatPrefix, object instance, object holder)
    {
        if (compatPrefix == null)
            return true;
        var controller = PressFromController();
        bool runOriginal;
        try
        {
            runOriginal = controller || compatPrefix.Invoke(null, new[] { instance, holder }) is not false;
        }
        catch (Exception e)
        {
            // Better the game's own handling than a press that does nothing.
            DsLog.Warn($"{compatPrefix.Name} failed, using the game's handling: {e.InnerException?.Message ?? e.Message}");
            runOriginal = true;
        }
        var key = compatPrefix.Name;
        var count = PressesLogged.GetValueOrDefault(key);
        if (count < 5)
        {
            PressesLogged[key] = count + 1;
            var game = IsUsingController();
            DsLog.Debug($"{instance?.GetType().Name} press: {(controller ? "controller" : "touch")}"
                + (game != controller ? $" (game is in {(game ? "controller" : "mouse")} mode)" : "") + ", "
                + (controller ? "game handles it" : runOriginal ? "compat passed it on" : "compat kept it (mark, then Confirm)"));
        }
        return runOriginal;
    }

    // ── map ────────────────────────────────────────────────────────────────

    private static bool IsCopy(object instance) => instance != null && ReferenceEquals(instance, _map);

    private static bool IsTopMap(object instance) => instance != null && ReferenceEquals(instance, _topMap);

    private static void MapReadyPostfix(object __instance)
    {
        if (__instance is not Control map)
            return;
        if (IsCopy(map))
        {
            // Nothing to go back from, and no controller to show hotkeys for.
            HideField(map, _mapBackButton);
            HideField(map, _mapLegendHotkey);
            HideField(map, _mapToolsHotkey);
            _copyDrawings = _mapDrawings?.GetValue(map);
            return;
        }
        // A new run's map, before the last one has left: the copy was of that one.
        if (_topMap != null && !ReferenceEquals(_topMap, map))
            MapExitPostfix(_topMap);
        _topMap = map;
        _topDrawings = _mapDrawings?.GetValue(map);
        // Not every version overrides _ExitTree, so there may be none to patch.
        map.TreeExiting += () => MapExitPostfix(map);
    }

    private static void HideField(Control map, FieldInfo field)
    {
        if (field?.GetValue(map) is CanvasItem item)
            item.Visible = false;
    }

    // The run's UI is going away: the copy with it, out of the tree at once.
    // Its rooms let go of the game's map (NMapScreen.Instance) as they
    // leave, and that is gone by the time a queued free would run.
    private static void MapExitPostfix(object __instance)
    {
        if (!IsTopMap(__instance))
            return;
        _topMap = null;
        if (MapValid())
        {
            try
            {
                _map.GetParent()?.RemoveChild(_map);
            }
            catch (Exception e)
            {
                DsLog.Warn($"could not take the map copy down: {e.Message}");
            }
            _map.QueueFree();
        }
        _map = null;
        _topDrawings = _copyDrawings = null;
        _mapSeed = null;
        _markerCoord = null;
        UpdateMapPlaceholder();
    }

    private static void MapInitializePostfix(object __instance)
    {
        if (IsTopMap(__instance))
            QueueRebuild();
    }

    // The seed is a different integer type in different versions; by __args.
    private static void MapSetMapPostfix(object __instance, object[] __args)
    {
        if (!IsTopMap(__instance) || __args.Length < 3)
            return;
        _mapSeed = __args[1];
        var clearDrawings = __args[2] is true;
        // A new act: the drawings were cleared up top; clear them below too.
        if (clearDrawings && _copyDrawings != null)
            TryMap(() => _drawClearAll?.Invoke(_copyDrawings, null), "clear the drawings");
        QueueRebuild();
    }

    // The coord is a struct of the game's; boxed through __args.
    private static void MapInitMarkerPostfix(object __instance, object[] __args)
    {
        if (!IsTopMap(__instance) || __args.Length == 0)
            return;
        // Placed by the rebuild: the copy's rooms are rebuilt then too, so
        // the coord's room may not exist on it yet.
        _markerCoord = __args[0];
        QueueRebuild();
    }

    private static void MapRefreshVotesPostfix(object __instance)
    {
        if (IsTopMap(__instance) && MapValid())
            TryMap(() => _mapRefreshVotes?.Invoke(_map, null), "refresh votes");
    }

    // Travel switched on or off up top: the copy shows the same rooms as
    // reachable (they pulse), and the dock's map tab rocks while it is on.
    private static void MapTravelPostfix(object __instance)
    {
        if (!IsTopMap(__instance))
            return;
        CopyTravelState();
        DsBottomScreen.Instance?.Dock.SetTraveling(_mapTravelGet?.Invoke(_topMap) == true);
        DsBottomScreen.Instance?.MarkChanged();
    }

    private static void CopyTravelState()
    {
        if (!MapValid() || !GodotObject.IsInstanceValid(_topMap))
            return;
        TryMap(() =>
        {
            foreach (var flag in new[] { _mapTravelEnabled, _mapDebugTravelEnabled })
            {
                if (flag?.CanWrite == true)
                    flag.SetValue(_map, flag.GetValue(_topMap));
            }
            _mapRefreshVisuals?.Invoke(_map, null);
        }, "copy the travel state");
    }

    // A room was entered: rebuild the copy, as the game draws the map for a
    // run in progress (travelled paths, the marker, what is reachable).
    private static void VisitedMapCoordPostfix() => QueueRebuild();

    private static bool TopMapOnlyPrefix(object __instance) => !IsCopy(__instance);

    // Should a room of the copy still leave after the game's map has gone,
    // its unsubscribing from that map finds nothing; nothing to undo then.
    private static int _pointExitFailures;

    private static Exception CopyPointExitFinalizer(object __instance, Exception __exception)
    {
        if (__exception is not NullReferenceException || __instance is not Node node
            || _topMap != null && GodotObject.IsInstanceValid(_topMap) && _topMap.IsAncestorOf(node))
            return __exception;
        if (_pointExitFailures++ == 0)
            DsLog.Debug("a map copy room left after the game's map; ignored");
        return null;
    }

    // Travel is the top map's. A room on the copy shows its tip and nothing
    // more when let go of.
    private static bool MapPointReleasePrefix(object __instance) =>
        __instance is not Node node || DsBottomScreen.Instance?.IsBottom(node) != true;

    private static void QueueRebuild()
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Callable.From(RebuildCopy).CallDeferred();
    }

    /// <summary>
    /// Makes the copy (once per run) and brings it level with the top map:
    /// run state, act map, marker, travel state. A new copy also takes the
    /// top map's drawings.
    /// </summary>
    private static void RebuildCopy()
    {
        _rebuildQueued = false;
        var screen = DsBottomScreen.Instance;
        if (screen == null || _topMap == null || !GodotObject.IsInstanceValid(_topMap))
            return;
        var runState = _mapRunState?.GetValue(_topMap);
        if (runState == null)
            return;
        var created = false;
        if (!MapValid())
        {
            if (!CreateCopy(screen))
                return;
            created = true;
        }
        TryMap(() =>
        {
            if (!ReferenceEquals(_mapRunState.GetValue(_map), runState))
                _mapInitialize?.Invoke(_map, new[] { runState });
            var actMap = _mapActMap?.GetValue(_topMap);
            if (actMap != null && actMap.GetType().Name != "NullActMap" && SeedOf(runState) is { } seed)
            {
                _mapSetMap?.Invoke(_map, new[] { actMap, seed, (object)false });
                var coord = AccessTools.Property(runState.GetType(), "CurrentMapCoord")?.GetValue(runState) ?? _markerCoord;
                if (coord != null)
                    _mapInitMarker?.Invoke(_map, new[] { coord });
            }
        }, "bring it up to date");
        CopyTravelState();
        if (created)
            CopyAllDrawings();
        // To the current room, as the game's map opens, unless a finger is
        // on it: it would jump out from under it.
        if (screen.Ready)
            RevealMap(_map, scrollToCurrent: !screen.Touching);
        UpdateMapPlaceholder();
        screen.MarkChanged();
    }

    private static object SeedOf(object runState)
    {
        if (_mapSeed != null)
            return _mapSeed;
        try
        {
            // What SetMap takes, converted from the run's seed.
            var type = _mapSetMap?.GetParameters() is { Length: > 1 } parameters ? parameters[1].ParameterType : null;
            var rng = AccessTools.Property(runState.GetType(), "Rng")?.GetValue(runState);
            var seed = rng == null ? null : AccessTools.Property(rng.GetType(), "Seed")?.GetValue(rng);
            return seed == null || type == null ? null : Convert.ChangeType(seed, type);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool CreateCopy(DsBottomScreen screen)
    {
        try
        {
            var path = string.IsNullOrEmpty(_topMap.SceneFilePath) ? MapScenePath : _topMap.SceneFilePath;
            var scene = ResourceLoader.Load<PackedScene>(path);
            if (scene?.Instantiate() is not Control copy)
            {
                DsLog.Warn($"map scene not found ({path}); no map on the bottom screen");
                return false;
            }
            copy.Name = "DsMap";
            copy.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            copy.OffsetLeft = copy.OffsetTop = copy.OffsetRight = copy.OffsetBottom = 0;
            // Always shown down here: SetMap works out which rooms are
            // reachable only on a visible map, and _Ready only processes one.
            copy.Visible = true;
            // Known as the copy before its _Ready runs.
            _map = copy;
            screen.Page("map").AddChild(copy);
            StyleText(screen);
            DsLog.Debug($"map copy made for the bottom screen ({path})");
            return true;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not make the map copy: {e.InnerException?.Message ?? e.Message}");
            if (MapValid())
                _map.QueueFree();
            _map = null;
            _copyDrawings = null;
            return false;
        }
    }

    // In mouse mode, as touches on it are: in controller mode the game's
    // map would give one of its rooms controller focus (TryGrabFocus).
    private static void TryMap(Action action, string what)
    {
        var mode = BeginTouchDispatch();
        try
        {
            action();
        }
        catch (Exception e)
        {
            DsLog.Warn($"map copy: could not {what}: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            EndTouchDispatch(mode);
        }
    }

    private static bool MapValid() => _map != null && GodotObject.IsInstanceValid(_map);

    private static void ResolveMap()
    {
        if (_mapType == null)
            return;
        _mapContainer = AccessTools.Field(_mapType, "_mapContainer");
        _mapTargetDrag = AccessTools.Field(_mapType, "_targetDragPos");
        _mapDragging = AccessTools.Field(_mapType, "_isDragging");
        _mapDrawingInput = AccessTools.Field(_mapType, "_drawingInput");
        _mapTween = AccessTools.Field(_mapType, "_tween");
        _mapBackstop = AccessTools.Field(_mapType, "_backstop");
        _mapLegend = AccessTools.Field(_mapType, "_mapLegend");
        _mapTools = AccessTools.Field(_mapType, "_drawingTools");
        _mapPoints = AccessTools.Field(_mapType, "_points");
        _mapRunState = AccessTools.Field(_mapType, "_runState");
        _mapDistY = AccessTools.Field(_mapType, "_distY");
        _mapActMap = AccessTools.Field(_mapType, "_map");
        _mapBackButton = AccessTools.Field(_mapType, "_backButton");
        _mapLegendHotkey = AccessTools.Field(_mapType, "_legendHotkeyIcon");
        _mapToolsHotkey = AccessTools.Field(_mapType, "_drawingToolsHotkeyIcon");
        _mapTravelEnabled = AccessTools.Property(_mapType, "IsTravelEnabled");
        _mapTravelGet = Getter<bool>(_mapTravelEnabled);
        _mapDebugTravelEnabled = AccessTools.Property(_mapType, "IsDebugTravelEnabled");
        _mapDrawings = AccessTools.Property(_mapType, "Drawings");
        _mapCanScroll = AccessTools.Method(_mapType, "CanScroll");
        _mapCancelActAnim = AccessTools.Method(_mapType, "TryCancelStartOfActAnim");
        _mapInitialize = AccessTools.Method(_mapType, "Initialize");
        _mapSetMap = AccessTools.Method(_mapType, "SetMap");
        _mapInitMarker = AccessTools.Method(_mapType, "InitMarker");
        _mapRefreshVotes = AccessTools.Method(_mapType, "RefreshAllMapPointVotes");
        _mapRefreshVisuals = AccessTools.Method(_mapType, "RefreshAllPointVisuals");
        // Consts; the literals are what v0.107.1 has.
        if (AccessTools.Field(_mapType, "_scrollLimitBottom") is { IsLiteral: true } low && low.GetRawConstantValue() is float l)
            _mapScrollLow = l;
        if (AccessTools.Field(_mapType, "_scrollLimitTop") is { IsLiteral: true } high && high.GetRawConstantValue() is float h)
            _mapScrollHigh = h;
        if (_mapContainer == null || _mapTargetDrag == null || _mapDragging == null)
            DsLog.Warn("map scroll fields not found; the map scrolls its own way");
        var missing = new List<string>();
        if (_mapRunState == null) missing.Add("_runState");
        if (_mapActMap == null) missing.Add("_map");
        if (_mapInitialize == null) missing.Add("Initialize");
        if (_mapSetMap == null) missing.Add("SetMap");
        if (_mapInitMarker == null) missing.Add("InitMarker");
        if (_mapTravelEnabled?.CanWrite != true) missing.Add("a settable IsTravelEnabled");
        if (_mapDrawings == null) missing.Add("Drawings");
        if (missing.Count > 0)
            DsLog.Warn($"map screen has no {string.Join(", ", missing)}; the bottom map may lag behind the top one");
    }

    private static void UpdateMapPlaceholder()
    {
        var screen = DsBottomScreen.Instance;
        if (screen == null)
            return;
        screen.SetPlaceholder("map", MapValid() ? "" : screen.PlaceholderText("map"));
    }

    /// <summary>
    /// Shows the closed map as it looks open: visible, processing (so it
    /// scrolls), with the parts the open/close tweens fade at full strength.
    /// </summary>
    private static void RevealMap(Control map, bool scrollToCurrent)
    {
        try
        {
            if (_mapTween?.GetValue(map) is Tween tween && GodotObject.IsInstanceValid(tween))
                tween.Kill();
            map.Visible = true;
            map.ProcessMode = Node.ProcessModeEnum.Inherit;
            SetModulate(map, _mapContainer, Colors.White);
            SetModulate(map, _mapLegend, Colors.White);
            SetModulate(map, _mapTools, Colors.White);
            SetModulate(map, _mapPoints, Colors.White);
            if (_mapBackstop?.GetValue(map) is CanvasItem backstop)
                backstop.Modulate = backstop.Modulate with { A = 0.85f };
            if (_mapLegend?.GetValue(map) is Control legend)
                legend.Position = legend.Position with { X = LegendX(map, legend) };
            ScaleMapTools(map);
            if (scrollToCurrent)
                ScrollToCurrent(map);
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not show the closed map: {e.Message}");
        }
    }

    // The drawing tools, larger, growing toward the middle of the map from
    // whichever edge or corner they sit at. The legend keeps the game's size
    // and is tucked into the bottom-right corner, where it covers the least
    // of the map, and lifted above anything it would sit on: the tools, and
    // any button of the screen's own there (the beta's share button, for
    // one), found by kind rather than by name. The open/close tweens fade
    // both and slide the legend along x to MapLegendX, which points at that
    // corner too; they never touch y or scale.
    private static bool _mapToolsLogged;

    private static void ScaleMapTools(Control map)
    {
        var scale = new Vector2(MapToolsScale, MapToolsScale);
        var toLocal = map.GetGlobalTransform().AffineInverse();
        Rect2? toolsRect = null;
        if (_mapTools?.GetValue(map) is Control tools && tools.Size != Vector2.Zero)
        {
            var area = map.GetGlobalRect();
            var at = (tools.GetGlobalRect().GetCenter() - area.Position) / area.Size;
            tools.PivotOffset = new Vector2(Side(at.X) * tools.Size.X, Side(at.Y) * tools.Size.Y);
            tools.Scale = scale;
            var global = tools.GetGlobalRect();
            toolsRect = new Rect2(toLocal * global.Position, toLocal.BasisXform(global.Size));
        }
        if (_mapLegend?.GetValue(map) is not Control legend || legend.Size == Vector2.Zero)
            return;
        legend.Scale = Vector2.One;
        var obstacles = MapButtons(map, legend, toLocal);
        if (toolsRect is { } toolsArea)
            obstacles.Add(("tools", toolsArea));
        var x = LegendX(map, legend);
        var bottom = map.Size.Y - LegendMargin;
        // Each lift can land it on another obstacle; one pass per obstacle settles it.
        for (var pass = 0; pass <= obstacles.Count; pass++)
        {
            var rect = new Rect2(x, bottom - legend.Size.Y, legend.Size);
            var hit = obstacles.Where(o => o.Rect.Intersects(rect)).Select(o => o.Rect.Position.Y).DefaultIfEmpty(float.NaN).Min();
            if (float.IsNaN(hit))
                break;
            bottom = hit - LegendMargin;
        }
        legend.Position = new Vector2(x, Math.Max(LegendMargin, bottom - legend.Size.Y));
        if (!_mapToolsLogged)
        {
            _mapToolsLogged = true;
            DsLog.Debug($"map {map.Size}: legend size {legend.Size} now bottom-right at y={legend.Position.Y:F0}, avoiding "
                + (obstacles.Count == 0 ? "nothing" : string.Join(", ", obstacles.Select(o => $"{o.Name} {o.Rect}"))));
        }
    }

    // The game's buttons are its own NClickableControl, not Godot's BaseButton.
    private static Type _clickableType;
    private static bool _clickableLooked;

    private static bool IsButton(Control control)
    {
        if (!_clickableLooked)
        {
            _clickableLooked = true;
            _clickableType = FindType("MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl");
        }
        return control is BaseButton || _clickableType?.IsInstanceOfType(control) == true;
    }

    /// <summary>
    /// The map screen's own visible buttons, in its local space: everything
    /// but the map itself, the legend and the drawing tools (measured scaled,
    /// by the caller).
    /// </summary>
    private static List<(string Name, Rect2 Rect)> MapButtons(Control map, Control legend, Transform2D toLocal)
    {
        var skip = new HashSet<Node> { legend };
        foreach (var field in new[] { "_mapContainer", "_drawingTools", "_backstop", "_mapBgContainer" })
            if (AccessTools.Field(_mapType, field)?.GetValue(map) is Node node)
                skip.Add(node);
        var found = new List<(string, Rect2)>();
        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (skip.Contains(child) || child is CanvasItem { Visible: false })
                    continue;
                if (child is Control button && button.Size != Vector2.Zero && IsButton(button))
                {
                    var global = button.GetGlobalRect();
                    found.Add((button.Name, new Rect2(toLocal * global.Position, toLocal.BasisXform(global.Size))));
                    continue;
                }
                Walk(child);
            }
        }
        Walk(map);
        return found;
    }

    private static float LegendX(Control map, Control legend) => map.Size.X - LegendMargin - legend.Size.X;

    private static void MapLegendXPostfix(object __instance, ref float __result)
    {
        if (__instance == _map && MapValid() && _mapLegend?.GetValue(_map) is Control legend)
            __result = LegendX(_map, legend);
    }

    // 0 for the near edge, 1 for the far one, 0.5 in between.
    private static float Side(float fraction) => fraction < 0.33f ? 0f : fraction > 0.67f ? 1f : 0.5f;

    private static void SetModulate(Control map, FieldInfo field, Color color)
    {
        if (field?.GetValue(map) is CanvasItem item)
            item.Modulate = color;
    }

    // Where Open() would put it: the current row near the bottom.
    private static void ScrollToCurrent(Control map)
    {
        var runState = _mapRunState?.GetValue(map);
        if (runState == null || _mapContainer?.GetValue(map) is not Control container)
            return;
        var coord = AccessTools.Property(runState.GetType(), "CurrentMapCoord")?.GetValue(runState);
        var row = coord == null ? 0 : Convert.ToInt32(AccessTools.Field(coord.GetType(), "row")?.GetValue(coord) ?? 0);
        var distY = _mapDistY?.GetValue(map) is float d ? d : 0f;
        var target = new Vector2(0f, _mapScrollLow + row * distY);
        container.Position = target;
        _mapTargetDrag?.SetValue(map, target);
    }

    // The map legend's header is a game label with the game's font set on
    // it, for the dock and placeholders.
    private static void StyleText(DsBottomScreen screen)
    {
        if (MapValid() && _map.GetNodeOrNull("MapLegend/Header") is Label header && header.HasThemeFontOverride("font"))
            screen.ApplyGameFont(header.GetThemeFont("font"));
    }

    // ── map drawing ────────────────────────────────────────────────────────

    // The draw button picks a mouse or a controller drawing tool from the
    // game's input mode. The bottom screen is for touch: its tool is always
    // the mouse one, even made outside a touch push (BeginTouchDispatch).
    // The top map's tool is the game's choice.
    private static void DrawingInputCreatePrefix(object drawings, out object __state)
    {
        __state = drawings is Node node && DsBottomScreen.Instance?.IsBottom(node) == true ? BeginTouchDispatch() : null;
    }

    private static void DrawingInputCreatePostfix(object __state) => EndTouchDispatch(__state);

    // Shared drawings. Each map keeps its own lines (a node renders in one
    // place only); every line the local player starts, extends, ends or
    // clears on one is done on the other too. Positions go through the
    // game's network form (centred, scaled to each drawing area), as a
    // peer's lines do. Only the top map sends to the network or changes the
    // mouse cursor: the copy's lines reach peers and the save as the top's.
    private static MethodInfo _drawBegin, _drawUpdate, _drawStop, _drawClear, _drawIsDrawing, _drawMode;
    private static MethodInfo _drawToNet, _drawFromNet, _drawLoad, _drawSave, _drawClearAll;
    private static object _topDrawings, _copyDrawings;
    private static bool _mirroring;
    private static int _mirrorFailures;

    private static void ApplyDrawingHooks(Harmony harmony)
    {
        var type = FindType(MapDrawingsType);
        if (type == null)
        {
            DsLog.Warn("no map drawings type; the two maps draw separately");
            return;
        }
        _drawBegin = AccessTools.Method(type, "BeginLineLocal");
        _drawUpdate = AccessTools.Method(type, "UpdateCurrentLinePositionLocal");
        _drawStop = AccessTools.Method(type, "StopLineLocal");
        _drawClear = AccessTools.Method(type, "ClearDrawnLinesLocal");
        _drawIsDrawing = AccessTools.Method(type, "IsLocalDrawing");
        _drawMode = AccessTools.Method(type, "GetLocalDrawingMode");
        _drawToNet = AccessTools.Method(type, "ToNetPosition");
        _drawFromNet = AccessTools.Method(type, "FromNetPosition");
        _drawLoad = AccessTools.Method(type, "LoadDrawings");
        _drawSave = AccessTools.Method(type, "GetSerializableMapDrawings");
        _drawClearAll = AccessTools.Method(type, "ClearAllLines");
        Patch(harmony, type, "QueueOrSendEvent", prefix: nameof(TopDrawingsOnlyPrefix));
        Patch(harmony, type, "UpdateLocalCursor", prefix: nameof(DrawingCursorPrefix));
        if (_drawIsDrawing == null || _drawMode == null || _drawToNet == null || _drawFromNet == null)
        {
            DsLog.Warn("map drawings can't be mirrored on this version; the two maps draw separately");
            return;
        }
        Patch(harmony, type, "BeginLineLocal", postfix: nameof(DrawBeginPostfix));
        Patch(harmony, type, "UpdateCurrentLinePositionLocal", postfix: nameof(DrawUpdatePostfix));
        Patch(harmony, type, "StopLineLocal", postfix: nameof(DrawStopPostfix));
        Patch(harmony, type, "ClearDrawnLinesLocal", postfix: nameof(DrawClearPostfix));
        Patch(harmony, type, "LoadDrawings", postfix: nameof(DrawLoadPostfix));
    }

    private static bool TopDrawingsOnlyPrefix(object __instance) => !ReferenceEquals(__instance, _copyDrawings);

    private static bool DrawingCursorPrefix(object __instance) => !_mirroring && !ReferenceEquals(__instance, _copyDrawings);

    private static object OtherDrawings(object drawings)
    {
        if (_mirroring || _topDrawings == null || _copyDrawings == null || !MapValid())
            return null;
        return ReferenceEquals(drawings, _topDrawings) ? _copyDrawings
            : ReferenceEquals(drawings, _copyDrawings) ? _topDrawings
            : null;
    }

    private static void Mirror(object source, Action<object> action)
    {
        if (OtherDrawings(source) is not { } other)
            return;
        _mirroring = true;
        try
        {
            action(other);
        }
        catch (Exception e)
        {
            if (_mirrorFailures++ < 5)
                DsLog.Warn($"could not mirror a map line: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            _mirroring = false;
        }
        DsBottomScreen.Instance?.MarkChanged();
    }

    private static object Translate(object from, object to, object position) =>
        _drawFromNet.Invoke(to, new[] { _drawToNet.Invoke(from, new[] { position }) });

    private static bool IsDrawing(object drawings) => _drawIsDrawing.Invoke(drawings, null) is true;

    private static void DrawBeginPostfix(object __instance, object[] __args) =>
        Mirror(__instance, other =>
        {
            // The line's own mode (drawing or erasing), so the other map's
            // drawing tool, on or off, doesn't matter.
            var mode = _drawMode.Invoke(__instance, new object[] { true });
            if (IsDrawing(other))
                _drawStop.Invoke(other, null);
            _drawBegin.Invoke(other, new[] { Translate(__instance, other, __args[0]), mode });
        });

    private static void DrawUpdatePostfix(object __instance, object[] __args) =>
        Mirror(__instance, other =>
        {
            if (IsDrawing(other))
                _drawUpdate.Invoke(other, new[] { Translate(__instance, other, __args[0]) });
        });

    private static void DrawStopPostfix(object __instance) =>
        Mirror(__instance, other =>
        {
            if (IsDrawing(other))
                _drawStop.Invoke(other, null);
        });

    private static void DrawClearPostfix(object __instance) => Mirror(__instance, other => _drawClear?.Invoke(other, null));

    // A save's drawings were loaded into the top map.
    private static void DrawLoadPostfix(object __instance)
    {
        if (!_mirroring && ReferenceEquals(__instance, _topDrawings))
            CopyAllDrawings();
    }

    /// <summary>The copy's lines replaced by the top map's.</summary>
    private static void CopyAllDrawings()
    {
        if (_topDrawings == null || _copyDrawings == null || _drawSave == null || _drawLoad == null || _drawClearAll == null)
            return;
        _mirroring = true;
        try
        {
            _drawClearAll.Invoke(_copyDrawings, null);
            if (_drawSave.Invoke(_topDrawings, null) is { } saved)
                _drawLoad.Invoke(_copyDrawings, new[] { saved });
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not copy the map drawings: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            _mirroring = false;
        }
        DsBottomScreen.Instance?.MarkChanged();
    }

    // ── piles ──────────────────────────────────────────────────────────────

    private static void CombatPileInitializePostfix(object __instance, object player)
    {
        if (player == null)
            return;
        var changed = !ReferenceEquals(player, _player) || !_inCombat;
        var started = !_inCombat;
        _player = player;
        if (started)
            SetCombatTabs(true);
        _inCombat = true;
        if (changed)
            ResetPiles("draw", "discard", "exhaust");
        // A fight starts on the draw pile, even before the surface is ready.
        if (started && DsBottomScreen.Instance is { } screen)
            screen.Show(CombatStartTab);
        // The dock takes the button's own icon (draw and exhaust have no
        // path the code names) and its count.
        if (DsBottomScreen.Instance?.Dock is not { } dock || PileButtonTab(__instance) is not { } tab)
            return;
        if (__instance is Node node && node.GetNodeOrNull("Icon") is { } icon)
            dock.SetIcon(tab, icon is TextureRect rect ? rect.Texture : icon.Get("texture").As<Texture2D>());
        UpdatePileCount(__instance, tab);
    }

    // The game's own counter moved (one card at a time, as its label does).
    private static void CombatPileCountPostfix(object __instance)
    {
        if (PileButtonTab(__instance) is { } tab)
            UpdatePileCount(__instance, tab);
    }

    // The combat UI is going away: the combat piles with it.
    private static void CombatPileExitPostfix()
    {
        if (!_inCombat)
            return;
        _inCombat = false;
        SetCombatTabs(false);
        // Its piles are gone with it: back to the map, for the next room.
        if (DsBottomScreen.Instance is { } screen && Array.IndexOf(CombatTabs, screen.ActiveTab) >= 0)
            screen.Show("map");
    }

    private static void DeckCountPostfix(object __instance)
    {
        try
        {
            if (ButtonPile.TryRead(__instance, out var pile) && PileCards.TryRead(pile, out var cards)
                && CardsCount.TryRead(cards, out var count))
                DsBottomScreen.Instance?.Dock.SetCount("deck", count);
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not read the deck count: {e.Message}");
        }
    }

    private static readonly string[] CombatTabs = { "draw", "discard", "exhaust" };

    // In combat the pile tabs light up (exhaust once it has a card, as the
    // game shows its button); out of it they dim and lose their counts. The
    // dock's idle loops calm down for the fight (DsDock.SetCombat).
    private static void SetCombatTabs(bool inCombat)
    {
        if (DsBottomScreen.Instance?.Dock is not { } dock)
            return;
        dock.SetCombat(inCombat);
        foreach (var tab in CombatTabs)
        {
            dock.SetEnabled(tab, inCombat && tab != "exhaust");
            if (!inCombat)
                dock.SetCount(tab, -1);
        }
    }

    // The pile buttons' and the piles' members, per type: each card moved reads them.
    private static readonly MemberReader<object> ButtonPile = new("_pile", field: true);
    private static readonly MemberReader<int> ButtonCount = new("_currentCount", field: true);
    private static readonly MemberReader<object> PileCards = new("Cards", field: false);
    private static readonly MemberReader<int> CardsCount = new("Count", field: false);

    private static string PileButtonTab(object button) =>
        ButtonPile.TryRead(button, out var pile) && pile != null ? TabForPile(pile) : null;

    private static void UpdatePileCount(object button, string tab)
    {
        if (DsBottomScreen.Instance?.Dock is not { } dock || !ButtonCount.TryRead(button, out var count))
            return;
        dock.SetCount(tab, count);
        if (tab == "exhaust")
            dock.SetEnabled(tab, count > 0);
    }

    private static void DeckButtonInitializePostfix(object player)
    {
        if (player == null || ReferenceEquals(player, _player) && PileScreens.ContainsKey("deck"))
            return;
        _player = player;
        ResetPiles("deck");
    }

    // A pile screen rebuilds its whole grid of card nodes on every change to
    // its pile. On PC only an open one exists; here all four live on, so a
    // hand draw would rebuild each of them once per card. Down here a hidden
    // one only notes it is stale and rebuilds when its tab is shown, and the
    // visible one rebuilds once a burst of changes settles: each change
    // pushes the rebuild a little later, never past a cap from the burst's
    // first change, and never while that pile's cards are still moving (a
    // rebuild would cut their motion short). One pile per frame at most.
    private const ulong PileQuietUsec = 120_000;
    private const ulong PileMaxWaitUsec = 350_000;
    private static readonly HashSet<Node> StalePiles = new();
    private static bool _refreshingPile;

    private struct PendingPile
    {
        public ulong First, Due;
        // Its leaving cards are gone: rebuild now, without animating them out again.
        public bool ExitsDone;
    }

    private static readonly Dictionary<Node, PendingPile> PendingPiles = new();
    private static readonly List<Node> PendingGone = new();
    private static readonly Action PileFrame = OnPileFrame;
    private static SceneTree _pileTree;

    private static bool PileContentsChangedPrefix(object __instance)
    {
        if (_refreshingPile || DsBottomScreen.Instance is not { } screen || __instance is not CanvasItem node || !screen.IsBottom(node))
            return true;
        // Already stale and not pending: its cards are on their way out, and
        // the rebuild after that takes this change too.
        if ((StalePiles.Add(node) || PendingPiles.ContainsKey(node)) && node.IsVisibleInTree())
            SchedulePile(node, exitsDone: false);
        return false;
    }

    private static void SchedulePile(Node node, bool exitsDone)
    {
        var now = Time.GetTicksUsec();
        if (!PendingPiles.TryGetValue(node, out var pending))
            pending = new PendingPile { First = now, Due = now + PileQuietUsec };
        else if (!pending.ExitsDone)
            pending.Due = Math.Min(now + PileQuietUsec, pending.First + PileMaxWaitUsec);
        if (exitsDone)
        {
            pending.ExitsDone = true;
            pending.Due = now;
        }
        PendingPiles[node] = pending;
        // Polled only while something is pending.
        if (_pileTree == null && node.IsInsideTree() && node.GetTree() is { } tree)
        {
            _pileTree = tree;
            tree.ProcessFrame += PileFrame;
        }
    }

    private static void OnPileFrame()
    {
        Node next = null;
        try
        {
            var now = Time.GetTicksUsec();
            var nextPending = default(PendingPile);
            PendingGone.Clear();
            foreach (var (node, pending) in PendingPiles)
            {
                // Hidden: no longer pending, but still stale, so its tab
                // rebuilds it when opened (EnsurePileScreen).
                if (!GodotObject.IsInstanceValid(node) || !StalePiles.Contains(node)
                    || node is CanvasItem item && !item.IsVisibleInTree())
                {
                    PendingGone.Add(node);
                    continue;
                }
                // Due, not mid-exit, and its last motion has played out.
                if (next != null || now < pending.Due || ExitingPiles.Contains(node) || CardsMoving(TabOf(node)))
                    continue;
                next = node;
                nextPending = pending;
            }
            foreach (var node in PendingGone)
            {
                PendingPiles.Remove(node);
                if (!GodotObject.IsInstanceValid(node))
                    StalePiles.Remove(node);
            }
            PendingGone.Clear();
            if (next != null)
            {
                PendingPiles.Remove(next);
                RefreshPile(next, animate: true, exitsDone: nextPending.ExitsDone);
            }
        }
        catch (Exception e)
        {
            // Dropped, not left stale: the next change schedules them afresh.
            DsLog.Warn($"pile refresh failed: {e.Message}");
            foreach (var node in PendingPiles.Keys)
                StalePiles.Remove(node);
            PendingPiles.Clear();
            // The one being refreshed left the pending list first; left
            // stale, its next change would count it as mid-exit and wait.
            if (next != null)
                StalePiles.Remove(next);
        }
        if (PendingPiles.Count == 0 && _pileTree != null)
        {
            _pileTree.ProcessFrame -= PileFrame;
            _pileTree = null;
        }
    }

    private static bool CardsMoving(string tab) =>
        tab != null && CardTweens.TryGetValue(tab, out var tween) && DsStyle.Running(tween);

    // A visible pile animates the change, as below; a hidden one (and one
    // brought up to date as its tab opens, which plays the entrance) just
    // rebuilds. Either way it keeps its scroll.
    private static void RefreshPile(Node node, bool animate = false, bool exitsDone = false)
    {
        TraceBegin("RefreshPile");
        try
        {
            if (!GodotObject.IsInstanceValid(node))
            {
                StalePiles.Remove(node);
                PendingPiles.Remove(node);
                return;
            }
            // Cards are leaving: it rebuilds, with whatever else changed, once they are gone.
            if (!StalePiles.Contains(node) || ExitingPiles.Contains(node))
                return;
            var tab = TabOf(node);
            animate = animate && tab != null && node is CanvasItem { } item && item.IsVisibleInTree();
            if (animate && !exitsDone && AnimateExits(tab, node))
                return;
            RebuildPile(node, tab, animate);
        }
        finally
        {
            TraceEnd();
        }
    }

    private static void RebuildPile(Node node, string tab, bool animate)
    {
        PendingPiles.Remove(node);
        if (!StalePiles.Remove(node) || !GodotObject.IsInstanceValid(node))
            return;
        PileSnapshot before = null;
        try
        {
            var grid = tab == null ? null : PileGrid(tab);
            before = grid == null ? null : Snapshot(grid, animate);
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not note a pile's layout: {e.InnerException?.Message ?? e.Message}");
        }
        // Its cards are about to go back to the game's pool: at rest first.
        SettleCards(tab);
        _refreshingPile = true;
        try
        {
            if (_pileRefresh != null)
            {
                _pileRefresh(node);
                NotePileRebuild();
            }
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not refresh a pile: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            _refreshingPile = false;
        }
        // The grid sizes its scroll area deferred; its limits are known after that.
        if (before != null)
            Callable.From(() => AfterRebuild(tab, node, before)).CallDeferred();
        DsBottomScreen.Instance?.MarkChanged();
    }

    // The lower screen's stats and trace markers.
    private static void NotePileRebuild() => DsBottomScreen.NotePileRebuild();
    private static void TraceBegin(string name) => DsBottomScreen.TraceBegin(name);
    private static void TraceEnd() => DsBottomScreen.TraceEnd();

    private static string TabOf(Node pileScreen)
    {
        foreach (var (tab, node) in PileScreens)
        {
            if (node == pileScreen)
                return tab;
        }
        return null;
    }

    // ── pile motion ────────────────────────────────────────────────────────

    // The game rebuilds a pile's whole grid on every change, and animates
    // only the card library's. Down here a change moves like that library:
    // cards that left drop and fade (NCardGrid.AnimateOut), then the grid is
    // rebuilt; cards still shown slide from where they were to their new
    // place, and new ones rise and fade in one after another
    // (NCardGrid.AnimateIn). All of it moves the card inside its holder,
    // never the holder, whose place is the grid's own (its scrolling moves
    // holders and swaps the cards in them). The game pools its cards, so
    // every one of these tweens is run to its end before the grid lets its
    // cards go (SettleCards), and when a finger takes the pile.
    //
    // A change's cards all move in one tween: one method tweener steps every
    // card from arrays made up front (PileMotion), rather than two property
    // tweeners per card. The curves are Godot's own (DsEase).
    private const float CardRise = 40f;
    private static readonly Dictionary<string, Tween> CardTweens = new();
    private static readonly Dictionary<string, PileMotion> Motions = new();
    private static readonly HashSet<Node> ExitingPiles = new();
    private static PropertyInfo _gridHolders;
    private static Func<object, object> _gridHoldersGet;
    private static Type _holderType;
    private static Func<object, object> _holderCardGet, _holderModelGet;

    private sealed class PileSnapshot
    {
        public float ScrollY;
        public bool AtTop;
        // Where each card shown was on screen, and how opaque; empty when not animating.
        public readonly Dictionary<object, (Vector2 Position, float Alpha)> Cards = new(ReferenceEqualityComparer.Instance);
    }

    // The grid's live holders (the rows in and near view), each with its
    // card and that card's model.
    private static List<(Control Holder, Control Card, object Model)> Displayed(Control grid, bool inViewOnly)
    {
        var shown = new List<(Control, Control, object)>();
        if (_gridHoldersGet?.Invoke(grid) is not System.Collections.IEnumerable holders)
            return shown;
        var area = grid.GetGlobalRect();
        foreach (var item in holders)
        {
            if (item is not Control { Visible: true } holder)
                continue;
            var type = holder.GetType();
            if (type != _holderType)
            {
                _holderType = type;
                _holderCardGet = Getter<object>(AccessTools.Property(type, "CardNode"));
                _holderModelGet = Getter<object>(AccessTools.Property(type, "CardModel"));
            }
            if (_holderCardGet?.Invoke(holder) is not Control card || !GodotObject.IsInstanceValid(card))
                continue;
            if (inViewOnly && !card.GetGlobalRect().Intersects(area) && !holder.GetGlobalRect().Intersects(area))
                continue;
            shown.Add((holder, card, _holderModelGet?.Invoke(holder)));
        }
        return shown;
    }

    private static PileSnapshot Snapshot(Control grid, bool withCards)
    {
        if (_gridContainerGet(grid) is not Control container)
            return null;
        var top = _gridTopGet(grid);
        var snapshot = new PileSnapshot { ScrollY = container.Position.Y, AtTop = Math.Abs(container.Position.Y - top) < 1f };
        if (withCards)
        {
            foreach (var (_, card, model) in Displayed(grid, inViewOnly: true))
            {
                if (model != null)
                    snapshot.Cards[model] = (card.GlobalPosition, card.Modulate.A);
            }
        }
        return snapshot;
    }

    /// <summary>Runs this pile's card animation to its end: every card at rest in its holder.</summary>
    private static void SettleCards(string tab)
    {
        if (tab == null)
            return;
        try
        {
            if (CardTweens.Remove(tab, out var tween) && GodotObject.IsInstanceValid(tween))
            {
                if (tween.IsValid())
                    tween.CustomStep(1000.0);
                tween.Kill();
            }
            // Cards set up for a motion whose tween never ran (it failed midway) too.
            if (Motions.TryGetValue(tab, out var motion))
                motion.Settle();
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not settle a pile's cards: {e.Message}");
        }
    }

    // One animation per pile at a time; a new one first ends the last.
    private static PileMotion BeginMotion(string tab)
    {
        SettleCards(tab);
        if (!Motions.TryGetValue(tab, out var motion))
            Motions[tab] = motion = new PileMotion();
        motion.Clear();
        return motion;
    }

    // exiting: the pile whose rebuild waits for these cards to be gone.
    private static void PlayMotion(string tab, PileMotion motion, Control container, Node exiting = null)
    {
        if (motion.Count == 0)
            return;
        var tween = DsStyle.Animate(container, motion.Length);
        tween.TweenMethod(motion.Step, 0f, motion.Length, motion.Length);
        if (exiting != null)
        {
            motion.Exiting = exiting;
            ExitingPiles.Add(exiting);
            tween.TweenCallback(motion.ExitsDone);
        }
        CardTweens[tab] = tween;
    }

    private struct CardMove
    {
        public Control Card;    // null once at rest
        public Color Tint;      // its modulate but for alpha, which may fade
        public Vector2 From, To;
        public float AlphaFrom, AlphaTo;
        public float Delay, Duration;
        public DsCurve Move, Fade;
        public bool Fades;
    }

    // One pile's cards in motion. Its two callables are made once per pile.
    private sealed class PileMotion
    {
        private CardMove[] _moves = new CardMove[16];
        public int Count;
        public float Length;
        public Node Exiting;
        public readonly Callable Step;
        public readonly Callable ExitsDone;

        public PileMotion()
        {
            Step = Callable.From<float>(StepTo);
            ExitsDone = Callable.From(OnExitsDone);
        }

        public void Clear()
        {
            Array.Clear(_moves, 0, Count);
            Count = 0;
            Length = 0f;
            Exiting = null;
        }

        // The card takes its first frame now: held there through its delay,
        // as a delayed property tween leaves it as it was set.
        public void Add(Control card, Vector2 from, Vector2 to, float alphaFrom, float alphaTo, bool fades,
            float delay, float duration, DsCurve move, DsCurve fade)
        {
            if (Count == _moves.Length)
                Array.Resize(ref _moves, Count * 2);
            var tint = card.Modulate;
            _moves[Count++] = new CardMove
            {
                Card = card, Tint = tint, From = from, To = to, AlphaFrom = alphaFrom, AlphaTo = alphaTo,
                Delay = delay, Duration = duration, Move = move, Fade = fade, Fades = fades,
            };
            card.Position = from;
            if (fades)
                card.Modulate = tint with { A = alphaFrom };
            Length = Math.Max(Length, delay + duration);
        }

        public void Settle() => StepTo(1e6f);

        private void StepTo(float t)
        {
            for (var i = 0; i < Count; i++)
            {
                ref var move = ref _moves[i];
                if (move.Card == null)
                    continue;
                var x = move.Duration > 0f ? (t - move.Delay) / move.Duration : 1f;
                if (x <= 0f)
                    continue;
                if (!GodotObject.IsInstanceValid(move.Card))
                {
                    move.Card = null;
                    continue;
                }
                x = Math.Min(x, 1f);
                move.Card.Position = move.From + (move.To - move.From) * DsEase.Apply(move.Move, x);
                if (move.Fades)
                    move.Card.Modulate = move.Tint with { A = move.AlphaFrom + (move.AlphaTo - move.AlphaFrom) * DsEase.Apply(move.Fade, x) };
                if (x >= 1f)
                    move.Card = null;
            }
        }

        private void OnExitsDone()
        {
            var node = Exiting;
            Exiting = null;
            if (node == null)
                return;
            ExitingPiles.Remove(node);
            SchedulePile(node, exitsDone: true);
        }
    }

    // A card's move, in its own units (its holder is scaled, and the grid with it).
    private static Vector2 ToCardUnits(Control card, Vector2 globalDelta)
    {
        var scale = card.GetParent() is Control parent ? parent.GetGlobalTransform().Scale : Vector2.One;
        return new Vector2(
            Math.Abs(scale.X) > 0.01f ? globalDelta.X / scale.X : globalDelta.X,
            Math.Abs(scale.Y) > 0.01f ? globalDelta.Y / scale.Y : globalDelta.Y);
    }

    // Cards shown that are no longer in the pile drop and fade; the rebuild
    // waits for them. False if none left.
    private static bool AnimateExits(string tab, Node node)
    {
        var grid = PileGrid(tab);
        if (grid == null || _gridContainerGet(grid) is not Control container
            || PileBindings.GetValueOrDefault(tab) is not { } pile
            || !PileCards.TryRead(pile, out var read) || read is not System.Collections.IEnumerable cards)
            return false;
        try
        {
            var inPile = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var model in cards)
                inPile.Add(model);
            List<Control> leaving = null;
            foreach (var (_, card, model) in Displayed(grid, inViewOnly: true))
            {
                if (model != null && !inPile.Contains(model))
                    (leaving ??= new List<Control>()).Add(card);
            }
            if (leaving == null)
                return false;
            var motion = BeginMotion(tab);
            foreach (var card in leaving)
            {
                var drop = ToCardUnits(card, new Vector2(0f, CardRise)).Y;
                var at = card.Position;
                motion.Add(card, at, at with { Y = at.Y + drop }, card.Modulate.A, 0f, fades: true,
                    0f, 0.2f, DsCurve.ExpoOut, DsCurve.Linear);
            }
            PlayMotion(tab, motion, container, exiting: node);
            return true;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not animate cards out: {e.InnerException?.Message ?? e.Message}");
            ExitingPiles.Remove(node);
            return false;
        }
    }

    // After a rebuild: back to where it was scrolled, then the new layout
    // moves in from the old one.
    private static void AfterRebuild(string tab, Node node, PileSnapshot before)
    {
        if (!GodotObject.IsInstanceValid(node) || TabOf(node) != tab)
            return;
        var grid = PileGrid(tab);
        if (grid == null || _gridContainerGet(grid) is not Control container)
            return;
        try
        {
            // At the top it stays at the top, which moves as rows come and go.
            var top = _gridTopGet(grid);
            var bottom = _gridBottomGet(grid);
            var y = before.AtTop ? top : Math.Clamp(before.ScrollY, Math.Min(top, bottom), Math.Max(top, bottom));
            SetPileScroll(grid, container, y, bottom, settle: true);
            if (before.Cards.Count == 0 || !node.IsInsideTree() || !((CanvasItem)node).IsVisibleInTree())
                return;
            var shown = Displayed(grid, inViewOnly: true);
            var arriving = 0;
            foreach (var (_, _, model) in shown)
            {
                if (model == null || !before.Cards.ContainsKey(model))
                    arriving++;
            }
            // The last motion ends before the new layout is read.
            var motion = BeginMotion(tab);
            var next = 0;
            foreach (var (_, card, model) in shown)
            {
                if (model != null && before.Cards.TryGetValue(model, out var was))
                {
                    var offset = ToCardUnits(card, was.Position - card.GlobalPosition);
                    if (offset.LengthSquared() < 1f && was.Alpha >= 0.99f)
                        continue;
                    card.Modulate = card.Modulate with { A = was.Alpha };
                    motion.Add(card, offset, Vector2.Zero, was.Alpha, 1f, fades: was.Alpha < 0.99f,
                        0f, 0.3f, DsCurve.CubicOut, DsCurve.ExpoOut);
                }
                else
                {
                    RiseIn(motion, card, next++ / (float)arriving * 0.2f);
                }
            }
            PlayMotion(tab, motion, container);
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not animate a pile change: {e.InnerException?.Message ?? e.Message}");
            SettleCards(tab);
        }
    }

    // NCardGrid.AnimateIn, on the card: up from below as it fades in.
    private static void RiseIn(PileMotion motion, Control card, float delay) =>
        motion.Add(card, new Vector2(0f, ToCardUnits(card, new Vector2(0f, CardRise)).Y), Vector2.Zero, 0f, 1f, fades: true,
            delay, 0.4f, DsCurve.BackOut, DsCurve.ExpoOut);

    // ── card touch ─────────────────────────────────────────────────────────

    // A card hovered by a finger down here: the game snaps its holder up to
    // hover size; here it pops there instead, so the touch is felt. Kept as
    // the holder's own hover tween, so the game's unhover replaces it and
    // eases back as usual. Cards on the top screen are left alone.
    private static FieldInfo _holderHoverTween;
    private static PropertyInfo _holderHoverScale, _holderSmallScale;

    private static void CardHoverPostfix(object __instance, bool isHovered)
    {
        if (!isHovered || __instance is not Control holder || DsBottomScreen.Instance is not { } screen || !screen.IsBottom(holder))
            return;
        try
        {
            if (_holderHoverTween == null || _holderHoverScale?.GetValue(holder) is not Vector2 hover
                || _holderSmallScale?.GetValue(holder) is not Vector2 small)
                return;
            holder.Scale = small;
            var tween = DsStyle.Animate(holder, 0.2);
            tween.TweenProperty(holder, DsNodePaths.Scale, hover, 0.18).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
            _holderHoverTween.SetValue(holder, tween);
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not animate a card touch: {e.Message}");
        }
    }

    // ── hover tips ─────────────────────────────────────────────────────────

    // Tips go into the game's container on the top screen. One for a card or
    // map point on the lower screen moves down there, next to it.
    private static void HoverTipPostfix(Control owner, Control __result) => MoveTip(__result, owner, retry: true);

    private static void MoveTip(Control tip, Control owner, bool retry)
    {
        var screen = DsBottomScreen.Instance;
        if (tip == null || screen == null || !GodotObject.IsInstanceValid(tip) || !screen.IsBottom(owner))
            return;
        try
        {
            var parent = tip.GetParent();
            if (parent == null)
            {
                // Added deferred; move it once it is in.
                if (retry)
                    Callable.From(() => MoveTip(tip, owner, retry: false)).CallDeferred();
                return;
            }
            if (parent == screen.HoverLayer)
                return;
            // It was placed in lower-screen canvas coordinates relative to the
            // container; keep that placement under the identity layer.
            var origin = parent is CanvasItem item ? item.GetGlobalTransform().Origin : Vector2.Zero;
            tip.Reparent(screen.HoverLayer, false);
            tip.Position += origin;
            // The game placed it by the owner's own size, not counting a
            // scaled-up parent (the pile screens): scale the whole placement
            // about the owner to match.
            var scale = owner.GetGlobalTransform().Scale.X / Math.Max(0.01f, owner.Scale.X);
            if (Math.Abs(scale - 1f) > 0.01f)
            {
                var anchor = owner.GlobalPosition;
                tip.Position = anchor + (tip.Position - anchor) * scale;
                tip.Scale = new Vector2(scale, scale);
            }
            Callable.From(() => FitTip(tip)).CallDeferred();
            screen.MarkChanged();
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not move a hover tip: {e.Message}");
        }
    }

    // The game keeps tips inside the top screen's size; keep them inside the lower canvas.
    private static void FitTip(Control tip)
    {
        var screen = DsBottomScreen.Instance;
        if (screen == null || !GodotObject.IsInstanceValid(tip))
            return;
        Rect2? bounds = null;
        foreach (var child in tip.GetChildren())
        {
            if (child is Control { Visible: true } control && control.Size != Vector2.Zero)
                bounds = bounds?.Merge(control.GetGlobalRect()) ?? control.GetGlobalRect();
        }
        if (bounds is not { } rect)
            return;
        var canvas = screen.CanvasSize;
        var shift = Vector2.Zero;
        if (rect.End.X > canvas.X)
            shift.X = canvas.X - rect.End.X;
        if (rect.Position.X + shift.X < 0)
            shift.X = -rect.Position.X;
        if (rect.End.Y > canvas.Y)
            shift.Y = canvas.Y - rect.End.Y;
        if (rect.Position.Y + shift.Y < 0)
            shift.Y = -rect.Position.Y;
        if (shift != Vector2.Zero)
            tip.Position += shift;
        screen.MarkChanged();
    }

    // A pile's PileType, read as its number and looked up, not named per call.
    private static readonly Dictionary<Type, Func<object, int>> PileTypeOf = new();
    private static readonly Dictionary<int, string> TabByPileType = new();
    private static readonly Dictionary<string, object> PileTypeValues = new();

    private static string TabForPile(object pile)
    {
        var type = pile.GetType();
        if (!PileTypeOf.TryGetValue(type, out var read))
        {
            var property = AccessTools.Property(type, "Type");
            read = property?.PropertyType.IsEnum == true ? Getter<int>(property) : null;
            if (read != null && TabByPileType.Count == 0)
            {
                foreach (var (tab, name) in TabToPileType)
                {
                    if (Enum.TryParse(property.PropertyType, name, out var value))
                        TabByPileType[Convert.ToInt32(value)] = tab;
                }
            }
            PileTypeOf[type] = read;
        }
        if (read != null)
            return TabByPileType.GetValueOrDefault(read(pile));
        var named = AccessTools.Property(type, "Type")?.GetValue(pile)?.ToString();
        foreach (var (tab, pileType) in TabToPileType)
        {
            if (pileType == named)
                return tab;
        }
        return null;
    }

    private static void ResetPiles(params string[] tabs)
    {
        StalePiles.RemoveWhere(n => !GodotObject.IsInstanceValid(n));
        foreach (var tab in tabs)
        {
            SettleCards(tab);
            if (PileScreens.Remove(tab, out var old) && GodotObject.IsInstanceValid(old))
                old.QueueFree();
            PileBindings.Remove(tab);
        }
        var screen = DsBottomScreen.Instance;
        if (screen != null && Array.IndexOf(tabs, screen.ActiveTab) >= 0)
            EnsurePileScreen(screen.ActiveTab);
    }

    private static void EnsurePileScreen(string tab)
    {
        var screen = DsBottomScreen.Instance;
        if (screen == null || _player == null || _pileScreenType == null || _getPile == null || _pileTypeEnum == null)
            return;
        object pile;
        try
        {
            if (!PileTypeValues.TryGetValue(tab, out var pileType))
                PileTypeValues[tab] = pileType = Enum.Parse(_pileTypeEnum, TabToPileType[tab]);
            pile = _getPile.Invoke(null, new[] { pileType, _player });
        }
        catch (Exception)
        {
            // Combat piles do not exist outside combat.
            pile = null;
        }
        if (pile == null)
        {
            ResetIfStale(tab, null);
            return;
        }
        if (PileScreens.TryGetValue(tab, out var existing) && GodotObject.IsInstanceValid(existing)
            && ReferenceEquals(PileBindings.GetValueOrDefault(tab), pile))
        {
            if (StalePiles.Contains(existing))
                RefreshPile(existing);
            return;
        }
        ResetIfStale(tab, pile);

        try
        {
            var path = AccessTools.Property(_pileScreenType, "ScenePath")?.GetValue(null) as string;
            var scene = path == null ? null : ResourceLoader.Load<PackedScene>(path);
            if (scene == null)
            {
                DsLog.Warn($"card pile scene not found ({path})");
                return;
            }
            var node = scene.Instantiate();
            node.Name = $"DsPile_{tab}";
            AccessTools.Property(_pileScreenType, "Pile").SetValue(node, pile);
            var page = screen.Page(tab);
            if (node is Control control)
            {
                // Laid out in a page-sized area divided by the scale, then
                // scaled up to fill the page: fewer, bigger cards per row.
                // Sized before it enters the tree: its grid lays out its
                // columns in _Ready and redoes that on a resize only when it
                // can scroll.
                control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                control.Scale = new Vector2(PileScale, PileScale);
                control.Size = page.Size / PileScale;
                control.Visible = true;
                if (ResizeHooked.Add(tab))
                    page.Resized += () => FitPileScreen(tab);
            }
            // Built at once: it is only created for the tab being shown.
            _refreshingPile = true;
            try
            {
                page.AddChild(node);
            }
            finally
            {
                _refreshingPile = false;
            }
            // Its back button closes the top screen's capstone; there is
            // nothing to go back from down here. Disabled, not just hidden:
            // enabled, it holds the newest binding for cancel/back, and the
            // game's hotkeys go to the newest binding only.
            if (node.GetNodeOrNull("BackButton") is CanvasItem back)
            {
                back.Visible = false;
                AccessTools.Method(back.GetType(), "Disable")?.Invoke(back, null);
            }
            FitPileChrome(node);
            PileScreens[tab] = node;
            PileBindings[tab] = pile;
            screen.SetPlaceholder(tab, "");
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not show {tab} pile: {e.InnerException?.Message ?? e.Message}");
        }
    }

    // ── pile scrolling ─────────────────────────────────────────────────────

    // A finger on a pile drives its grid directly. The grid's own drag only
    // starts on a press that no card took, and a card takes nearly every
    // press; its wheel steps ease in after the finger. Here the grid follows
    // the finger 1:1, with some give past either end, and its own update
    // eases it back once the finger lets go.
    private const float OverscrollGive = 0.35f;
    private static Type _gridType;
    private static FieldInfo _gridDragging, _gridTarget, _gridContainer, _gridScrollbar;
    private static PropertyInfo _gridLimitTop, _gridLimitBottom;
    private static MethodInfo _gridAllocate, _scrollbarSet;
    private static Func<object, object> _gridContainerGet;
    private static Func<object, float> _gridTopGet, _gridBottomGet;
    private static Action<object> _gridAllocateCall;
    private static Func<object, object> _pileGridGet;
    private static Action<object> _pileRefresh;

    private static void ResolvePileScreen()
    {
        if (_pileScreenType == null)
            return;
        _pileGridGet = Getter<object>(AccessTools.Field(_pileScreenType, "_grid"));
        try
        {
            _pileRefresh = Caller(AccessTools.Method(_pileScreenType, "OnPileContentsChanged"));
        }
        catch (AmbiguousMatchException)
        {
        }
        if (_pileRefresh == null)
            DsLog.Warn("card pile screen has no OnPileContentsChanged; lower-screen piles won't follow changes");
        // Interpreted delegates would cost more than the reflection they replace.
        DsLog.Debug($"game members read through {(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled ? "compiled" : "interpreted")} delegates");
    }

    private static Control PileGrid(string tab)
    {
        if (tab == null || !PileScreens.TryGetValue(tab, out var screen) || !GodotObject.IsInstanceValid(screen)
            || _pileGridGet?.Invoke(screen) is not Control grid)
            return null;
        var type = grid.GetType();
        if (type != _gridType)
        {
            _gridType = type;
            _gridDragging = AccessTools.Field(type, "_isDragging");
            _gridTarget = AccessTools.Field(type, "_targetDrag");
            _gridContainer = AccessTools.Field(type, "_scrollContainer");
            _gridScrollbar = AccessTools.Field(type, "_scrollbar");
            _gridLimitTop = AccessTools.Property(type, "ScrollLimitTop");
            _gridLimitBottom = AccessTools.Property(type, "ScrollLimitBottom");
            _gridAllocate = AccessTools.Method(type, "AllocateCardHolders");
            _gridHolders = AccessTools.Property(type, "CurrentlyDisplayedCardHolders");
            _gridContainerGet = Getter<object>(_gridContainer);
            _gridTopGet = Getter<float>(_gridLimitTop);
            _gridBottomGet = Getter<float>(_gridLimitBottom);
            _gridAllocateCall = Caller(_gridAllocate);
            _gridHoldersGet = Getter<object>(_gridHolders);
            _scrollbarSet = null;
        }
        return _gridDragging == null || _gridTarget == null || _gridContainer == null || _gridLimitTop == null || _gridLimitBottom == null
            ? null
            : grid;
    }

    /// <summary>The bottom map has a drawing tool on: a finger there draws.</summary>
    public static bool IsDrawing(string tab) =>
        tab == "map" && MapValid() && _mapDrawingInput?.GetValue(_map) is Node drawing
        && GodotObject.IsInstanceValid(drawing) && !drawing.IsQueuedForDeletion();

    /// <summary>A finger on this tab can scroll it: a pile with a grid, or the map when not drawing.</summary>
    public static bool CanScroll(string tab) => tab == "map" ? MapCanScroll() : PileGrid(tab) != null;

    /// <summary>A finger took hold of (or let go of) this tab's map or pile. False if it can't be held.</summary>
    public static bool HoldScroll(string tab, bool held) => tab == "map" ? HoldMap(held) : HoldPile(tab, held);

    /// <summary>Moves this tab's map or pile by a distance in lower-canvas pixels. False once past an end.</summary>
    public static bool ScrollBy(string tab, float canvasDy) => tab == "map" ? ScrollMap(canvasDy) : ScrollPile(tab, canvasDy);

    // The map eases toward its drag target every frame; down here it is put
    // straight on it, so it stays under the finger, and the game's ease only
    // pulls it back from past either end. With a drawing tool on, one finger
    // draws (CanScroll says no) and two still scroll.
    private static bool MapCanScroll()
    {
        if (!MapValid() || _mapContainer == null || _mapTargetDrag == null || _mapDragging == null)
            return false;
        return !IsDrawing("map");
    }

    // Two fingers scroll even with a drawing tool on; only one finger draws.
    private static bool HoldMap(bool held)
    {
        if (!MapValid() || _mapContainer == null || _mapTargetDrag == null || _mapDragging == null)
            return false;
        try
        {
            if (held)
            {
                // The act's opening scroll gives way to a finger, as to a mouse.
                _mapCancelActAnim?.Invoke(_map, null);
                if (_mapCanScroll?.Invoke(_map, null) is false)
                    return false;
            }
            _mapDragging.SetValue(_map, held);
            if (_mapContainer.GetValue(_map) is Control container)
                _mapTargetDrag.SetValue(_map, container.Position);
            return true;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not hold the map: {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    private static bool ScrollMap(float canvasDy)
    {
        if (!MapValid() || _mapContainer?.GetValue(_map) is not Control container)
            return false;
        try
        {
            var scale = container.GetGlobalTransform().Scale.Y;
            var dy = canvasDy / (Math.Abs(scale) > 0.01f ? scale : 1f);
            var y = container.Position.Y;
            var next = y + dy;
            if (next < _mapScrollLow || next > _mapScrollHigh)
                next = y + dy * OverscrollGive;
            container.Position = container.Position with { Y = next };
            _mapTargetDrag.SetValue(_map, container.Position);
            return next >= _mapScrollLow && next <= _mapScrollHigh;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not scroll the map: {e.Message}");
            return false;
        }
    }

    private static bool HoldPile(string tab, bool held)
    {
        var grid = PileGrid(tab);
        if (grid == null)
            return false;
        try
        {
            // Scrolling swaps cards between holders: nothing may be mid-move.
            if (held)
                SettleCards(tab);
            _gridDragging.SetValue(grid, held);
            if (_gridContainerGet(grid) is Control container)
                _gridTarget.SetValue(grid, container.Position.Y);
            return true;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not hold the pile: {e.Message}");
            return false;
        }
    }

    private static bool ScrollPile(string tab, float canvasDy)
    {
        var grid = PileGrid(tab);
        if (grid == null || _gridContainerGet(grid) is not Control container)
            return false;
        try
        {
            var top = _gridTopGet(grid);
            var bottom = _gridBottomGet(grid);
            float low = Math.Min(top, bottom), high = Math.Max(top, bottom);
            var dy = canvasDy / PileScale;
            var y = container.Position.Y;
            var next = y + dy;
            if (next < low || next > high)
                next = y + dy * OverscrollGive;
            // A change landing mid-fling: its cards stop moving rather than drift onto others.
            if (CardTweens.ContainsKey(tab))
                SettleCards(tab);
            SetPileScroll(grid, container, next, bottom, settle: false);
            return next >= low && next <= high;
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not scroll the pile: {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    // Puts the grid at y, its window of holders and its scrollbar with it.
    // The grid moves its window a row per call (once a frame as it scrolls);
    // settle: as many calls as it takes to catch up with a jump.
    private static void SetPileScroll(Control grid, Control container, float y, float bottom, bool settle)
    {
        _gridTarget.SetValue(grid, y);
        container.Position = container.Position with { Y = y };
        if (_gridAllocateCall != null)
        {
            for (var i = 0; i < (settle ? 64 : 1); i++)
            {
                var first = settle && _gridHoldersGet?.Invoke(grid) is IEnumerable<object> holders ? holders.FirstOrDefault() : null;
                _gridAllocateCall(grid);
                if (!settle || first == null || ReferenceEquals(first, (_gridHoldersGet(grid) as IEnumerable<object>)?.FirstOrDefault()))
                    break;
            }
        }
        if (_gridScrollbar.GetValue(grid) is Node bar && Math.Abs(bottom) > 0.01f)
        {
            _scrollbarSet ??= AccessTools.Method(bar.GetType(), "SetValueWithoutAnimation");
            _scrollbarSet?.Invoke(bar, new object[] { (double)(Math.Clamp(y / bottom, 0f, 1f) * 100f) });
        }
    }

    /// <summary>
    /// The pile's entrance as the card library's (NCardGrid.AnimateIn): the
    /// cards in view rise and fade in one after another. Each card moves in
    /// its holder; the holders' places are the grid's, so it is never fought.
    /// </summary>
    public static void StaggerPile(string tab)
    {
        var grid = PileGrid(tab);
        if (grid == null || _gridContainerGet(grid) is not Control container)
            return;
        try
        {
            var shown = Displayed(grid, inViewOnly: true);
            var count = Math.Min(shown.Count, 40);
            if (count == 0)
                return;
            var motion = BeginMotion(tab);
            for (var i = 0; i < count; i++)
                RiseIn(motion, shown[i].Card, i / (float)count * 0.2f);
            PlayMotion(tab, motion, container);
        }
        catch (Exception e)
        {
            DsLog.Warn($"could not animate the pile in: {e.Message}");
            SettleCards(tab);
        }
    }

    // Made for a full screen under the top bar: the grid starts below where
    // the bar would be, and fades cards to black at its top and bottom
    // edges. Down here that is a black band over the cards; drop both, and
    // start it below the corner tabs instead. The caption box stays only
    // where there is a caption (none for the deck).
    private static void FitPileChrome(Node pileScreen)
    {
        if (pileScreen.GetNodeOrNull("CardGrid") is Control grid)
        {
            if (grid.GetNodeOrNull("BorderGradient") is CanvasItem gradient)
                gradient.Visible = false;
            grid.OffsetTop = DsDock.CornerPlateHeight / PileScale;
            AccessTools.Method(grid.GetType(), "ReflowColumns")?.Invoke(grid, null);
        }
        if (pileScreen.GetNodeOrNull("BottomText") is CanvasItem caption
            && pileScreen.GetNodeOrNull("BottomText/MarginContainer/BottomLabel") is CanvasItem { Visible: false })
            caption.Visible = false;
    }

    private static void FitPileScreen(string tab)
    {
        if (!PileScreens.TryGetValue(tab, out var node) || node is not Control control || !GodotObject.IsInstanceValid(control)
            || DsBottomScreen.Instance is not { } screen)
            return;
        SettleCards(tab);
        control.Size = screen.Page(tab).Size / PileScale;
        // See EnsurePileScreen: the grid won't reflow by itself.
        if (_pileGridGet?.Invoke(control) is Node grid)
            AccessTools.Method(grid.GetType(), "ReflowColumns")?.Invoke(grid, null);
    }

    private static void ResetIfStale(string tab, object pile)
    {
        SettleCards(tab);
        if (PileScreens.Remove(tab, out var old) && GodotObject.IsInstanceValid(old))
            old.QueueFree();
        PileBindings.Remove(tab);
        if (pile == null && DsBottomScreen.Instance is { } screen)
            screen.SetPlaceholder(tab, screen.PlaceholderText(tab));
    }
    // ── compiled member access ─────────────────────────────────────────────

    // Game members read on every card moved or every frame: compiled once
    // into delegates rather than looked up by name and run through
    // reflection each time. Null where the member is missing (another game
    // version), as the lookup was; reflection where code can't be compiled.
    private sealed class MemberReader<T>
    {
        private readonly string _name;
        private readonly bool _field;
        private readonly Dictionary<Type, Func<object, T>> _byType = new();

        public MemberReader(string name, bool field)
        {
            _name = name;
            _field = field;
        }

        public bool TryRead(object instance, out T value)
        {
            value = default;
            if (instance == null)
                return false;
            var type = instance.GetType();
            if (!_byType.TryGetValue(type, out var read))
                _byType[type] = read = Getter<T>(_field ? AccessTools.Field(type, _name) : AccessTools.Property(type, _name));
            if (read == null)
                return false;
            value = read(instance);
            return true;
        }
    }

    private static Func<object, T> Getter<T>(MemberInfo member)
    {
        if (member is not (FieldInfo or PropertyInfo { CanRead: true }))
            return null;
        var field = member as FieldInfo;
        var property = member as PropertyInfo;
        try
        {
            var instance = Expr.Parameter(typeof(object));
            var isStatic = field?.IsStatic ?? property.GetGetMethod(true).IsStatic;
            var target = isStatic ? null : Expr.Convert(instance, member.DeclaringType);
            Expr read = field != null ? Expr.Field(target, field) : Expr.Property(target, property);
            var body = read.Type == typeof(T) ? read : Expr.Convert(read, typeof(T));
            return Expr.Lambda<Func<object, T>>(body, instance).Compile();
        }
        catch (Exception e)
        {
            NoteNotCompiled(member, e);
            return field != null ? o => Cast<T>(field.GetValue(o)) : o => Cast<T>(property.GetValue(o));
        }
    }

    private static Func<object, T> Calling<T>(MethodInfo method)
    {
        if (method == null)
            return null;
        try
        {
            var instance = Expr.Parameter(typeof(object));
            var call = method.IsStatic ? Expr.Call(method) : Expr.Call(Expr.Convert(instance, method.DeclaringType), method);
            Expr body = call.Type == typeof(T) ? call : Expr.Convert(call, typeof(T));
            return Expr.Lambda<Func<object, T>>(body, instance).Compile();
        }
        catch (Exception e)
        {
            NoteNotCompiled(method, e);
            return o => Cast<T>(method.Invoke(o, null));
        }
    }

    private static Action<object> Caller(MethodInfo method)
    {
        if (method == null)
            return null;
        try
        {
            var instance = Expr.Parameter(typeof(object));
            var call = method.IsStatic ? Expr.Call(method) : Expr.Call(Expr.Convert(instance, method.DeclaringType), method);
            return Expr.Lambda<Action<object>>(call, instance).Compile();
        }
        catch (Exception e)
        {
            NoteNotCompiled(method, e);
            return o => method.Invoke(o, null);
        }
    }

    private static T Cast<T>(object value)
    {
        if (value is T typed)
            return typed;
        try
        {
            return value is IConvertible && typeof(T).IsPrimitive ? (T)Convert.ChangeType(value, typeof(T)) : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static void NoteNotCompiled(MemberInfo member, Exception e) =>
        DsLog.Debug($"{member.DeclaringType?.Name}.{member.Name} read through reflection: {e.Message}");
}
