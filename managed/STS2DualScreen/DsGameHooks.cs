using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

// Where the bottom screen meets the game.
//
// Map: the game's NMapScreen is moved into the bottom screen's Map page as
// soon as it exists. It keeps all of its own behaviour (opening at the end of
// a room, travel, drawing, the back button); it simply renders and takes
// input down there. When it opens, the bottom screen switches to the Map tab,
// the top screen shows a one-line hint, and the controller drives the map.
// It no longer covers the top screen, so closing it skips the fade-out: the
// Map tab always shows it, closed (look and scroll only) or open.
//
// Piles: each pile tab hosts a fresh instance of the game's NCardPileScreen
// bound to the live CardPile, so it updates as cards move. The pile buttons
// on the top screen switch the bottom tab instead of opening an overlay.
//
// All game types are looked up by name. If one is missing (a game update),
// that feature logs and stays off; nothing here throws into the game.
internal static class DsGameHooks
{
    private const string MapScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen";
    private const string CardPileScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen";
    private const string DeckViewScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen";
    private const string CombatCardPileType = "MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile";
    private const string TopBarDeckButtonType = "MegaCrit.sts2.Core.Nodes.TopBar.NTopBarDeckButton";
    private const string PileTypeType = "MegaCrit.Sts2.Core.Entities.Cards.PileType";
    private const string PileTypeExtensionsType = "MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions";
    private const string MapPointType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint";
    private const string MapDrawingInputType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapDrawingInput";
    private const string ControllerMapDrawingInputType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NControllerMapDrawingInput";
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
    private static Type _screenContextType;
    private static MethodInfo _tapPreviewPrefix;
    private static MethodInfo _rewardSelectPrefix;
    private static MethodInfo _chooseSelectPrefix;
    private static PropertyInfo _runInstance;
    private static MethodInfo _overlayOpen;
    private static PropertyInfo _contextInstance;
    private static MethodInfo _contextCurrent, _contextFocusDefault;

    // NMapScreen members, resolved once: some are read on every forwarded
    // controller event and every scrolled frame.
    private static PropertyInfo _mapIsOpen;
    private static FieldInfo _mapContainer, _mapTargetDrag, _mapDragging, _mapDrawingInput, _mapTween;
    private static FieldInfo _mapBackstop, _mapLegend, _mapTools, _mapPoints, _mapRunState, _mapDistY;
    private static MethodInfo _mapCanScroll, _mapCancelActAnim;
    private static float _mapScrollLow = -600f, _mapScrollHigh = 1800f;
    private static bool _focusOverridden;
    private static Control _pendingMap;
    private static Control _map;
    private static object _player;
    private static bool _inCombat;

    /// <summary>A fight is on: its card piles exist.</summary>
    public static bool InCombat => _inCombat;

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

        Patch(harmony, _mapType, "_Ready", postfix: nameof(MapReadyPostfix));
        Patch(harmony, _mapType, "Open", postfix: nameof(MapOpenPostfix));
        Patch(harmony, _mapType, "Close", postfix: nameof(MapClosePostfix));
        Patch(harmony, _mapType, "AnimClose", prefix: nameof(MapAnimClosePrefix));
        Patch(harmony, _mapType, "get_MapLegendX", postfix: nameof(MapLegendXPostfix));
        Patch(harmony, FindType(MapPointType), "OnRelease", prefix: nameof(MapPointReleasePrefix));
        Patch(harmony, FindType(MapDrawingInputType), "Create", prefix: nameof(DrawingInputCreatePrefix), postfix: nameof(DrawingInputCreatePostfix));
        Patch(harmony, FindType(ControllerMapDrawingInputType), "_Process", prefix: nameof(ControllerDrawingProcessPrefix), postfix: nameof(ControllerDrawingProcessPostfix));
        _controllerManagerType = FindType(ControllerManagerType);
        _screenContextType = FindType(ActiveScreenContextType);
        if (_screenContextType != null)
        {
            _contextInstance = AccessTools.Property(_screenContextType, "Instance");
            _contextCurrent = AccessTools.Method(_screenContextType, "GetCurrentScreen");
            _contextFocusDefault = AccessTools.Method(_screenContextType, "FocusOnDefaultControl");
        }
        // The "Controller detected" / "Mouse detected" toast; the mode switch
        // itself happens before this and is untouched.
        Patch(harmony, _controllerManagerType, "ControlModeChanged", prefix: nameof(SkipPrefix));
        Patch(harmony, FindType(MouseModeMapDrawingInputType), "_Input", prefix: nameof(NotQueuedForDeletionPrefix));
        GuardTouchOnlyPatches(harmony);
        // Touching the lower screen takes window focus from the game, and the
        // game drops every hotkey and controller action while unfocused.
        Patch(harmony, FindType(GameType), "IsGameFocusedWindow", postfix: nameof(GameFocusedPostfix));
        _overlayOpen = FindType(PlatformUtilType) is { } platform ? AccessTools.Method(platform, "IsPlatformOverlayOpen") : null;
        _runInstance = FindType(RunType) is { } run ? AccessTools.Property(run, "Instance") : null;
        var hoverTips = FindType(HoverTipSetType);
        // The single-tip overload calls this one.
        var createAndShow = hoverTips == null ? null : AccessTools.GetDeclaredMethods(hoverTips)
            .FirstOrDefault(m => m.Name == "CreateAndShow" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType.IsGenericType);
        Patch(harmony, hoverTips, createAndShow, "CreateAndShow", postfix: nameof(HoverTipPostfix));
        Patch(harmony, hoverTips, "CreateAndShowMapPointHistory", postfix: nameof(HoverTipPostfix));
        Patch(harmony, FindType(CombatCardPileType), "Initialize", postfix: nameof(CombatPileInitializePostfix));
        Patch(harmony, FindType(CombatCardPileType), "OnRelease", postfix: nameof(CombatPileReleasePostfix));
        Patch(harmony, FindType(CombatCardPileType), "AddCard", postfix: nameof(CombatPileCountPostfix));
        Patch(harmony, FindType(CombatCardPileType), "RemoveCard", postfix: nameof(CombatPileCountPostfix));
        Patch(harmony, FindType(CombatCardPileType), "_ExitTree", postfix: nameof(CombatPileExitPostfix));
        Patch(harmony, FindType(TopBarDeckButtonType), "Initialize", postfix: nameof(DeckButtonInitializePostfix));
        Patch(harmony, FindType(TopBarDeckButtonType), "OnPileContentsChanged", postfix: nameof(DeckCountPostfix));
        Patch(harmony, _pileScreenType, "ShowScreen", prefix: nameof(ShowPileScreenPrefix));
        Patch(harmony, _pileScreenType, "OnPileContentsChanged", prefix: nameof(PileContentsChangedPrefix));
        Patch(harmony, FindType(DeckViewScreenType), "ShowScreen", prefix: nameof(ShowDeckScreenPrefix));
        Patch(harmony, FindType(MainMenuType), "_Ready", postfix: nameof(MainMenuReadyPostfix));
    }

    private static void MainMenuReadyPostfix()
    {
        if (MainMenuReady)
            return;
        MainMenuReady = true;
        DsLog.Info("main menu ready; lower screen wakes");
    }

    private static void Patch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null)
    {
        MethodInfo target = null;
        try
        {
            target = type == null ? null : AccessTools.Method(type, method);
        }
        catch (AmbiguousMatchException)
        {
        }
        Patch(harmony, type, target, method, prefix, postfix);
    }

    private static void Patch(Harmony harmony, Type type, MethodInfo target, string method, string prefix = null, string postfix = null)
    {
        if (type == null)
        {
            DsLog.Info($"hook skipped, type not found for {method} ({prefix ?? postfix})");
            return;
        }
        try
        {
            if (target == null)
            {
                DsLog.Info($"hook skipped, {type.Name}.{method} not found");
                return;
            }
            harmony.Patch(
                target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(DsGameHooks), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(DsGameHooks), postfix)
            );
            DsLog.Info($"hooked {type.Name}.{method}");
        }
        catch (Exception e)
        {
            DsLog.Info($"hook {type.Name}.{method} failed: {e.Message}");
        }
    }

    // ── bottom screen lifecycle ────────────────────────────────────────────

    public static void OnBottomScreenReady(DsBottomScreen screen)
    {
        SetCombatTabs(_inCombat);
        if (_pendingMap != null)
            AdoptMap(_pendingMap);
    }

    public static void OnSurfaceReady()
    {
        if (!MapValid())
            return;
        if (IsMapOpen())
            DsBottomScreen.Instance?.Show("map");
        else
            RevealMap(_map, scrollToCurrent: false);
    }

    public static void OnTabShown(string tab)
    {
        if (TabToPileType.ContainsKey(tab))
            EnsurePileScreen(tab);
        else if (tab == "map")
            UpdateMapPlaceholder();
    }

    /// <summary>The open map is the game's current screen, so the controller is its.</summary>
    public static bool MapOwnsInput() => IsMapOpen() && ReferenceEquals(CurrentScreen(), _map);

    public static string CurrentScreenName() => CurrentScreen()?.GetType().Name ?? "none";

    /// <summary>The open map's rooms pulse forever (NNormalMapPoint), and so does its marker.</summary>
    public static bool MapAmbient() => IsMapOpen();

    /// <summary>A run is loaded (NRun exists). True when that can't be told, so nothing goes missing.</summary>
    public static bool InRun()
    {
        if (_runInstance == null)
            return true;
        try
        {
            return _runInstance.GetValue(null) != null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static object ScreenContext() => _contextInstance?.GetValue(null);

    private static object CurrentScreen()
    {
        var context = ScreenContext();
        return context == null ? null : _contextCurrent?.Invoke(context, null);
    }

    /// <summary>
    /// Nothing on the top screen has controller focus: give it to the current
    /// screen's default control, as the game does when a screen opens. Not in
    /// combat, where an unfocused hand is normal (targeting, for one).
    /// </summary>
    public static void RestoreTopFocus(string why)
    {
        Callable.From(() =>
        {
            try
            {
                if (MapOwnsInput() || ((Engine.GetMainLoop() as SceneTree)?.Root.GuiGetFocusOwner() != null))
                    return;
                var context = ScreenContext();
                var current = CurrentScreen();
                if (current == null || current.GetType().Name.Contains("CombatRoom"))
                    return;
                _contextFocusDefault?.Invoke(context, null);
                DsLog.Info($"top-screen focus restored ({why}, screen={current.GetType().Name})");
            }
            catch (Exception e)
            {
                DsLog.Info($"could not restore focus: {e.InnerException?.Message ?? e.Message}");
            }
        }).CallDeferred();
    }

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
                DsLog.Info("game window focused again");
            }
            return;
        }
        if (DsBottomScreen.Instance?.Ready != true)
            return;
        try
        {
            if (_overlayOpen?.Invoke(null, null) is true)
                return;
        }
        catch (Exception)
        {
        }
        __result = true;
        if (!_focusOverridden)
        {
            _focusOverridden = true;
            DsLog.Info("game window lost focus to the lower screen; still taking input");
        }
    }

    private static object ControllerManager() =>
        _controllerManagerType == null ? null : AccessTools.Property(_controllerManagerType, "Instance")?.GetValue(null);

    private static bool SkipPrefix() => false;

    // A stopped drawing tool is freed at the end of the frame; touches later
    // in that frame would start a line with no drawing mode and throw.
    private static bool NotQueuedForDeletionPrefix(object __instance) =>
        __instance is not Node node || !node.IsQueuedForDeletion();

    private static bool IsUsingController()
    {
        var manager = ControllerManager();
        return manager != null && AccessTools.Property(_controllerManagerType, "IsUsingController")?.GetValue(manager) is true;
    }

    // Whether the press being handled came from a controller. The game's own
    // IsUsingController flips to mouse on any mouse event at all, and one
    // turns up now and then while a controller is in hand, after which every
    // A press went down the touch path. So a press is judged by the last
    // press the window saw, controller (pad, key, action) or pointer (touch,
    // click); the game's mode only decides before either has happened.
    private static ulong _lastPadPressUsec, _lastPointerPressUsec, _lastTouchUsec;
    private static int _strayMouseLogged;

    /// <summary>Every event the top window receives, before anything handles it.</summary>
    public static void NoteInput(InputEvent e)
    {
        var now = Time.GetTicksUsec();
        switch (e)
        {
            case InputEventScreenTouch { Pressed: true }:
                _lastPointerPressUsec = _lastTouchUsec = now;
                break;
            case InputEventMouseButton { Pressed: true } mouse:
                _lastPointerPressUsec = now;
                // A click no touch made: the kind that throws the game out of
                // controller mode. Godot's own click for a touch (device -1)
                // arrives just before that touch, so it is not one.
                if (mouse.Device != -1 && now - _lastTouchUsec > 200_000 && _strayMouseLogged++ < 5)
                    DsLog.Info($"mouse press without a touch: {mouse.AsText()} device={mouse.Device} at {mouse.Position}");
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
            DsLog.Info("no compat shop confirmation; nothing to guard");
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
                DsLog.Info($"no {compatPrefix} on {type.Name}.{method}; nothing to guard");
                return null;
            }
            harmony.Unpatch(target, patch.PatchMethod);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DsGameHooks), guard));
            DsLog.Info($"{type.Name}.{method}: {compatPrefix} limited to touch ({patch.owner})");
            return patch.PatchMethod;
        }
        catch (Exception e)
        {
            DsLog.Info($"could not guard {type.Name}.{method}: {e.Message}");
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
            DsLog.Info($"{compatPrefix.Name} failed, using the game's handling: {e.InnerException?.Message ?? e.Message}");
            runOriginal = true;
        }
        var key = compatPrefix.Name;
        var count = PressesLogged.GetValueOrDefault(key);
        if (count < 5)
        {
            PressesLogged[key] = count + 1;
            var game = IsUsingController();
            DsLog.Info($"{instance?.GetType().Name} press: {(controller ? "controller" : "touch")}"
                + (game != controller ? $" (game is in {(game ? "controller" : "mouse")} mode)" : "") + ", "
                + (controller ? "game handles it" : runOriginal ? "compat passed it on" : "compat kept it (mark, then Confirm)"));
        }
        return runOriginal;
    }

    // ── map ────────────────────────────────────────────────────────────────

    private static void MapReadyPostfix(object __instance)
    {
        if (__instance is not Control map)
            return;
        // Reparenting inside _Ready is not allowed; wait a frame.
        Callable.From(() => AdoptMap(map)).CallDeferred();
    }

    private static void AdoptMap(Control map)
    {
        var screen = DsBottomScreen.Instance;
        if (screen == null)
        {
            _pendingMap = map;
            return;
        }
        _pendingMap = null;
        try
        {
            if (!GodotObject.IsInstanceValid(map))
                return;
            var page = screen.Page("map");
            if (map.GetParent() != page)
            {
                map.Reparent(page, false);
                map.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                map.OffsetLeft = map.OffsetTop = map.OffsetRight = map.OffsetBottom = 0;
            }
            _map = map;
            if (screen.Ready && !IsMapOpen())
                RevealMap(map, scrollToCurrent: true);
            UpdateMapPlaceholder();
            StyleText(screen);
            DsLog.Info("map screen moved to the bottom screen");
        }
        catch (Exception e)
        {
            DsLog.Info($"could not move the map: {e.Message}");
        }
    }

    private static void MapOpenPostfix(object __instance)
    {
        if (__instance != _map)
            return;
        UpdateMapPlaceholder();
        var screen = DsBottomScreen.Instance;
        if (screen?.Ready != true)
            return;
        screen.Show("map");
        ScaleMapTools(_map);
        screen.Dock.SetTraveling(true);
        screen.SetTopHint(true);
        screen.ReleaseTopFocus();
    }

    private static void MapClosePostfix(object __instance)
    {
        if (__instance != _map)
            return;
        var screen = DsBottomScreen.Instance;
        if (screen == null)
            return;
        screen.SetTopHint(false);
        screen.Dock.SetTraveling(false);
        screen.ReleaseBottomFocus();
        // Close(animateOut: false) hides it directly.
        if (screen.Ready)
            RevealMap(_map, scrollToCurrent: false);
        UpdateMapPlaceholder();
        if (_inCombat && screen.ActiveTab == "map")
            screen.Show("draw");
        // Opening the map let go of top-screen focus; the screen under it
        // (rewards, say) does not take it back by itself.
        if (PressFromController())
            RestoreTopFocus("map closed");
    }

    private static bool MapAnimClosePrefix(object __instance, ref System.Threading.Tasks.Task __result)
    {
        if (__instance != _map || DsBottomScreen.Instance?.Ready != true)
            return true;
        __result = System.Threading.Tasks.Task.CompletedTask;
        return false;
    }

    // A travelable point on the closed map opens the map first, so the
    // journey starts from the open state the game expects.
    private static void MapPointReleasePrefix(object __instance)
    {
        if (!MapValid() || IsMapOpen() || DsBottomScreen.Instance?.Ready != true)
            return;
        if (AccessTools.Property(__instance.GetType(), "IsTravelable")?.GetValue(__instance) is true)
            OpenMap();
    }

    private static bool MapValid() => _map != null && GodotObject.IsInstanceValid(_map);

    private static bool IsMapOpen() => MapValid() && _mapIsOpen?.GetValue(_map) is true;

    private static void ResolveMap()
    {
        if (_mapType == null)
            return;
        _mapIsOpen = AccessTools.Property(_mapType, "IsOpen");
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
        _mapCanScroll = AccessTools.Method(_mapType, "CanScroll");
        _mapCancelActAnim = AccessTools.Method(_mapType, "TryCancelStartOfActAnim");
        // Consts; the literals are what v0.107.1 has.
        if (AccessTools.Field(_mapType, "_scrollLimitBottom") is { IsLiteral: true } low && low.GetRawConstantValue() is float l)
            _mapScrollLow = l;
        if (AccessTools.Field(_mapType, "_scrollLimitTop") is { IsLiteral: true } high && high.GetRawConstantValue() is float h)
            _mapScrollHigh = h;
        if (_mapContainer == null || _mapTargetDrag == null || _mapDragging == null)
            DsLog.Info("map scroll fields not found; the map scrolls its own way");
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
            DsLog.Info($"could not show the closed map: {e.Message}");
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
            DsLog.Info($"map {map.Size}: legend size {legend.Size} now bottom-right at y={legend.Position.Y:F0}, avoiding "
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

    // The game's own on-screen notice (the controller toast) is the closest
    // match for our top-screen hint. The map legend's header is a game label
    // with the game's font set on it, for the dock and placeholders.
    private static void StyleText(DsBottomScreen screen)
    {
        if (MapValid() && _map.GetNodeOrNull("MapLegend/Header") is Label header && header.HasThemeFontOverride("font"))
            screen.ApplyGameFont(header.GetThemeFont("font"));
        if (ControllerManager() is Node manager && manager.GetNodeOrNull("Label") is Label label)
            screen.ApplyGameTextStyle(label);
    }

    // ── map drawing ────────────────────────────────────────────────────────

    // The draw button picks a mouse or a controller drawing tool from the
    // game's input mode. Touches on the lower screen never reach the game's
    // controller manager, so after any controller use it would hand a finger
    // the stick-driven cursor. A button just pressed by touch gets the mouse tool.
    private static void DrawingInputCreatePrefix(bool stopOnMouseRelease, out bool __state)
    {
        __state = false;
        if (stopOnMouseRelease || DsBottomScreen.Instance?.TouchRecent != true)
            return;
        var manager = ControllerManager();
        var usingController = manager == null ? null : AccessTools.Property(_controllerManagerType, "IsUsingController");
        if (usingController?.GetValue(manager) is not true)
            return;
        usingController.SetValue(manager, false);
        __state = true;
    }

    private static void DrawingInputCreatePostfix(bool __state)
    {
        if (__state)
            AccessTools.Property(_controllerManagerType, "IsUsingController")?.SetValue(ControllerManager(), true);
    }

    // The controller drawing cursor clamps itself to the top screen's game
    // rect. On the lower screen, clamp to the bottom canvas instead.
    private static void ControllerDrawingProcessPrefix(object __instance, out Vector2? __state)
    {
        __state = null;
        if (__instance is Node node && node.GetViewport() is SubViewport
            && AccessTools.Field(__instance.GetType(), "_cursor")?.GetValue(__instance) is Control cursor)
            __state = cursor.GlobalPosition;
    }

    private static void ControllerDrawingProcessPostfix(object __instance, double delta, Vector2? __state)
    {
        if (__state is not { } before || DsBottomScreen.Instance is not { } screen)
            return;
        var type = __instance.GetType();
        if (AccessTools.Field(type, "_direction")?.GetValue(__instance) is not Vector2 direction || direction.Length() <= 0f
            || AccessTools.Field(type, "_cursor")?.GetValue(__instance) is not Control cursor)
            return;
        cursor.GlobalPosition = (before + direction * 700f * (float)delta).Clamp(Vector2.Zero, screen.CanvasSize);
        var drawings = AccessTools.Field(type, "_drawings")?.GetValue(__instance);
        if (drawings is CanvasItem item && AccessTools.Method(drawings.GetType(), "IsLocalDrawing")?.Invoke(drawings, null) is true)
            AccessTools.Method(drawings.GetType(), "UpdateCurrentLinePositionLocal")
                ?.Invoke(drawings, new object[] { item.GetGlobalTransform().AffineInverse() * cursor.GlobalPosition });
    }

    private static void OpenMap()
    {
        if (!MapValid() || IsMapOpen())
            return;
        try
        {
            AccessTools.Method(_mapType, "Open")?.Invoke(_map, new object[] { true });
        }
        catch (Exception e)
        {
            DsLog.Info($"could not open the map: {e.InnerException?.Message ?? e.Message}");
        }
    }

    // ── piles ──────────────────────────────────────────────────────────────

    private static void CombatPileInitializePostfix(object __instance, object player)
    {
        if (player == null)
            return;
        var changed = !ReferenceEquals(player, _player) || !_inCombat;
        _player = player;
        if (!_inCombat)
            SetCombatTabs(true);
        _inCombat = true;
        if (changed)
            ResetPiles("draw", "discard", "exhaust");
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
    }

    private static void DeckCountPostfix(object __instance)
    {
        try
        {
            var pile = AccessTools.Field(__instance.GetType(), "_pile")?.GetValue(__instance);
            var cards = pile == null ? null : AccessTools.Property(pile.GetType(), "Cards")?.GetValue(pile);
            if (cards != null && AccessTools.Property(cards.GetType(), "Count")?.GetValue(cards) is int count)
                DsBottomScreen.Instance?.Dock.SetCount("deck", count);
        }
        catch (Exception e)
        {
            DsLog.Info($"could not read the deck count: {e.Message}");
        }
    }

    private static readonly string[] CombatTabs = { "draw", "discard", "exhaust" };

    // In combat the pile tabs light up (exhaust once it has a card, as the
    // game shows its button); out of it they dim and lose their counts.
    private static void SetCombatTabs(bool inCombat)
    {
        if (DsBottomScreen.Instance?.Dock is not { } dock)
            return;
        foreach (var tab in CombatTabs)
        {
            dock.SetEnabled(tab, inCombat && tab != "exhaust");
            if (!inCombat)
                dock.SetCount(tab, -1);
        }
    }

    private static string PileButtonTab(object button)
    {
        var pile = button == null ? null : AccessTools.Field(button.GetType(), "_pile")?.GetValue(button);
        return pile == null ? null : TabForPile(pile);
    }

    private static void UpdatePileCount(object button, string tab)
    {
        if (DsBottomScreen.Instance?.Dock is not { } dock || AccessTools.Field(button.GetType(), "_currentCount")?.GetValue(button) is not int count)
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

    // An empty pile shows a thought bubble instead of opening; switch the
    // bottom tab anyway, so the pile button and its hotkey always do.
    private static void CombatPileReleasePostfix(object __instance)
    {
        var screen = DsBottomScreen.Instance;
        if (screen?.Ready != true || !_inCombat)
            return;
        var pile = AccessTools.Field(__instance.GetType(), "_pile")?.GetValue(__instance);
        if (pile == null || AccessTools.Property(pile.GetType(), "IsEmpty")?.GetValue(pile) is not true)
            return;
        if (TabForPile(pile) is { } tab)
            screen.Show(tab);
    }

    // A pile screen rebuilds its whole grid of card nodes on every change to
    // its pile. On PC only an open one exists; here all four live on, so a
    // hand draw would rebuild each of them once per card. Down here a hidden
    // one only notes it is stale and rebuilds when its tab is shown, and the
    // visible one rebuilds once a burst of changes settles.
    private const double PileRefreshDelay = 0.12;
    private static readonly HashSet<Node> StalePiles = new();
    private static bool _refreshingPile;

    private static bool PileContentsChangedPrefix(object __instance)
    {
        if (_refreshingPile || DsBottomScreen.Instance is not { } screen || __instance is not CanvasItem node || !screen.IsBottom(node))
            return true;
        if (StalePiles.Add(node) && node.IsVisibleInTree())
            node.GetTree().CreateTimer(PileRefreshDelay).Timeout += () => RefreshPile(node);
        return false;
    }

    private static void RefreshPile(Node node)
    {
        if (!StalePiles.Remove(node) || !GodotObject.IsInstanceValid(node))
            return;
        _refreshingPile = true;
        try
        {
            AccessTools.Method(_pileScreenType, "OnPileContentsChanged")?.Invoke(node, null);
        }
        catch (Exception e)
        {
            DsLog.Info($"could not refresh a pile: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            _refreshingPile = false;
        }
        DsBottomScreen.Instance?.MarkChanged();
    }

    // Instead of an overlay on the top screen, show the pile on the bottom one.
    // Callers ignore the returned screen, so it is left null.
    private static bool ShowPileScreenPrefix(object pile)
    {
        var screen = DsBottomScreen.Instance;
        if (screen?.Ready != true || pile == null)
            return true;
        var tab = TabForPile(pile);
        if (tab == null)
            return true;
        screen.Show(tab);
        return false;
    }

    private static bool ShowDeckScreenPrefix(object player)
    {
        var screen = DsBottomScreen.Instance;
        if (screen?.Ready != true)
            return true;
        if (player != null && !ReferenceEquals(player, _player))
        {
            _player = player;
            ResetPiles("deck");
        }
        screen.Show("deck");
        return false;
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
            DsLog.Info($"could not move a hover tip: {e.Message}");
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

    private static string TabForPile(object pile)
    {
        var type = AccessTools.Property(pile.GetType(), "Type")?.GetValue(pile)?.ToString();
        foreach (var (tab, pileType) in TabToPileType)
        {
            if (pileType == type)
                return tab;
        }
        return null;
    }

    private static void ResetPiles(params string[] tabs)
    {
        StalePiles.RemoveWhere(n => !GodotObject.IsInstanceValid(n));
        foreach (var tab in tabs)
        {
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
            var pileType = Enum.Parse(_pileTypeEnum, TabToPileType[tab]);
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
                DsLog.Info($"card pile scene not found ({path})");
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
            DsLog.Info($"could not show {tab} pile: {e.InnerException?.Message ?? e.Message}");
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

    private static Control PileGrid(string tab)
    {
        if (tab == null || !PileScreens.TryGetValue(tab, out var screen) || !GodotObject.IsInstanceValid(screen)
            || AccessTools.Field(_pileScreenType, "_grid")?.GetValue(screen) is not Control grid)
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
            _scrollbarSet = null;
        }
        return _gridDragging == null || _gridTarget == null || _gridContainer == null || _gridLimitTop == null || _gridLimitBottom == null
            ? null
            : grid;
    }

    /// <summary>A finger on this tab can scroll it: a pile with a grid, or the map when not drawing.</summary>
    public static bool CanScroll(string tab) => tab == "map" ? MapCanScroll() : PileGrid(tab) != null;

    /// <summary>A finger took hold of (or let go of) this tab's map or pile. False if it can't be held.</summary>
    public static bool HoldScroll(string tab, bool held) => tab == "map" ? HoldMap(held) : HoldPile(tab, held);

    /// <summary>Moves this tab's map or pile by a distance in lower-canvas pixels. False once past an end.</summary>
    public static bool ScrollBy(string tab, float canvasDy) => tab == "map" ? ScrollMap(canvasDy) : ScrollPile(tab, canvasDy);

    // The map eases toward its drag target every frame; down here it is put
    // straight on it, so it stays under the finger, and the game's ease only
    // pulls it back from past either end. Drawing tools keep the finger.
    private static bool MapCanScroll()
    {
        if (!MapValid() || _mapContainer == null || _mapTargetDrag == null || _mapDragging == null)
            return false;
        return _mapDrawingInput?.GetValue(_map) is not Node drawing || !GodotObject.IsInstanceValid(drawing) || drawing.IsQueuedForDeletion();
    }

    private static bool HoldMap(bool held)
    {
        if (!MapCanScroll())
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
            DsLog.Info($"could not hold the map: {e.InnerException?.Message ?? e.Message}");
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
            DsLog.Info($"could not scroll the map: {e.Message}");
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
            _gridDragging.SetValue(grid, held);
            if (_gridContainer.GetValue(grid) is Control container)
                _gridTarget.SetValue(grid, container.Position.Y);
            return true;
        }
        catch (Exception e)
        {
            DsLog.Info($"could not hold the pile: {e.Message}");
            return false;
        }
    }

    private static bool ScrollPile(string tab, float canvasDy)
    {
        var grid = PileGrid(tab);
        if (grid == null || _gridContainer.GetValue(grid) is not Control container)
            return false;
        try
        {
            var top = (float)_gridLimitTop.GetValue(grid);
            var bottom = (float)_gridLimitBottom.GetValue(grid);
            float low = Math.Min(top, bottom), high = Math.Max(top, bottom);
            var dy = canvasDy / PileScale;
            var y = container.Position.Y;
            var next = y + dy;
            if (next < low || next > high)
                next = y + dy * OverscrollGive;
            _gridTarget.SetValue(grid, next);
            container.Position = container.Position with { Y = next };
            _gridAllocate?.Invoke(grid, null);
            if (_gridScrollbar.GetValue(grid) is Node bar && Math.Abs(bottom) > 0.01f)
            {
                _scrollbarSet ??= AccessTools.Method(bar.GetType(), "SetValueWithoutAnimation");
                _scrollbarSet?.Invoke(bar, new object[] { (double)(Math.Clamp(next / bottom, 0f, 1f) * 100f) });
            }
            return next >= low && next <= high;
        }
        catch (Exception e)
        {
            DsLog.Info($"could not scroll the pile: {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The card library's entrance, alpha only: the cards in view fade in one
    /// after another. Their positions are the grid's, so it is never fought.
    /// </summary>
    public static void StaggerPile(string tab)
    {
        var grid = PileGrid(tab);
        if (grid == null || _gridContainer.GetValue(grid) is not Control container || DsBottomScreen.Instance is not { } screen)
            return;
        try
        {
            var area = grid.GetGlobalRect();
            var holders = new List<CanvasItem>();
            foreach (var child in container.GetChildren())
            {
                if (child is Control { Visible: true } holder && holder.GetGlobalRect().Intersects(area))
                    holders.Add(holder);
                if (holders.Count == 40)
                    break;
            }
            if (holders.Count == 0)
                return;
            var tween = DsStyle.Animate(container, 0.6).SetParallel(true);
            for (var i = 0; i < holders.Count; i++)
            {
                var holder = holders[i];
                holder.Modulate = holder.Modulate with { A = 0f };
                tween.TweenProperty(holder, "modulate:a", 1f, 0.4).SetDelay(i / (double)holders.Count * 0.2)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            }
            screen.MarkChanged(container, 0.6);
        }
        catch (Exception e)
        {
            DsLog.Info($"could not animate the pile in: {e.Message}");
        }
    }

    // Made for a full screen under the top bar: the grid starts below where
    // the bar would be, and fades cards to black at its top and bottom
    // edges. Down here that is a black band over the cards; drop both. The
    // caption box stays only where there is a caption (none for the deck).
    private static void FitPileChrome(Node pileScreen)
    {
        if (pileScreen.GetNodeOrNull("CardGrid") is Control grid)
        {
            if (grid.GetNodeOrNull("BorderGradient") is CanvasItem gradient)
                gradient.Visible = false;
            grid.OffsetTop = 0;
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
        control.Size = screen.Page(tab).Size / PileScale;
        // See EnsurePileScreen: the grid won't reflow by itself.
        if (AccessTools.Field(_pileScreenType, "_grid")?.GetValue(control) is Node grid)
            AccessTools.Method(grid.GetType(), "ReflowColumns")?.Invoke(grid, null);
    }

    private static void ResetIfStale(string tab, object pile)
    {
        if (PileScreens.Remove(tab, out var old) && GodotObject.IsInstanceValid(old))
            old.QueueFree();
        PileBindings.Remove(tab);
        if (pile == null && DsBottomScreen.Instance is { } screen)
            screen.SetPlaceholder(tab, screen.PlaceholderText(tab));
    }
}
