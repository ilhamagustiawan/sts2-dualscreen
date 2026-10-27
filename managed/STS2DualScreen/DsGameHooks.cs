using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

// Where the bottom screen meets the game.
//
// Map: the game's NMapScreen is moved into the bottom screen's Map page as
// soon as it exists. It keeps all of its own behaviour (opening at the end of
// a room, travel, drawing, the back button); it simply renders and takes
// input down there. When it opens, the bottom screen switches to the Map tab
// and the top screen shows a one-line hint.
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
    private static Control _pendingMap;
    private static Control _map;
    private static object _player;
    private static bool _inCombat;
    private static readonly Dictionary<string, Node> PileScreens = new();
    private static readonly Dictionary<string, object> PileBindings = new();

    public static void Apply(Harmony harmony)
    {
        _mapType = AccessTools.TypeByName(MapScreenType);
        _pileScreenType = AccessTools.TypeByName(CardPileScreenType);
        _pileTypeEnum = AccessTools.TypeByName(PileTypeType);
        var extensions = AccessTools.TypeByName(PileTypeExtensionsType);
        _getPile = extensions == null ? null : AccessTools.Method(extensions, "GetPile");

        Patch(harmony, _mapType, "_Ready", postfix: nameof(MapReadyPostfix));
        Patch(harmony, _mapType, "Open", postfix: nameof(MapOpenPostfix));
        Patch(harmony, _mapType, "Close", postfix: nameof(MapClosePostfix));
        Patch(harmony, AccessTools.TypeByName(CombatCardPileType), "Initialize", postfix: nameof(CombatPileInitializePostfix));
        Patch(harmony, AccessTools.TypeByName(TopBarDeckButtonType), "Initialize", postfix: nameof(DeckButtonInitializePostfix));
        Patch(harmony, _pileScreenType, "ShowScreen", prefix: nameof(ShowPileScreenPrefix));
        Patch(harmony, AccessTools.TypeByName(DeckViewScreenType), "ShowScreen", prefix: nameof(ShowDeckScreenPrefix));
    }

    private static void Patch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null)
    {
        if (type == null)
        {
            DsLog.Info($"hook skipped, type not found for {method} ({prefix ?? postfix})");
            return;
        }
        try
        {
            var target = AccessTools.Method(type, method);
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
        if (_pendingMap != null)
            AdoptMap(_pendingMap);
    }

    public static void OnSurfaceReady()
    {
        if (_map != null && IsMapOpen())
            DsBottomScreen.Instance?.Show("map");
    }

    public static void OnTabShown(string tab)
    {
        if (TabToPileType.ContainsKey(tab))
            EnsurePileScreen(tab);
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
            UpdateMapPlaceholder();
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
        screen.SetTopHint(true);
    }

    private static void MapClosePostfix(object __instance)
    {
        if (__instance != _map)
            return;
        UpdateMapPlaceholder();
        var screen = DsBottomScreen.Instance;
        if (screen == null)
            return;
        screen.SetTopHint(false);
        if (_inCombat && screen.ActiveTab == "map")
            screen.Show("draw");
    }

    private static bool IsMapOpen() =>
        _map != null && GodotObject.IsInstanceValid(_map) && AccessTools.Property(_mapType, "IsOpen")?.GetValue(_map) is true;

    private static void UpdateMapPlaceholder()
    {
        var screen = DsBottomScreen.Instance;
        if (screen == null)
            return;
        screen.SetPlaceholder("map", IsMapOpen() ? "" : _map == null ? "The map shows here once a run starts." : "Tap to view the map.");
    }

    /// <summary>Tapping the closed Map page opens the map, as the top bar's map button does.</summary>
    public static void OpenMapFromBottom()
    {
        if (_map == null || !GodotObject.IsInstanceValid(_map) || IsMapOpen())
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

    private static void CombatPileInitializePostfix(object player)
    {
        if (player == null)
            return;
        var changed = !ReferenceEquals(player, _player) || !_inCombat;
        _player = player;
        _inCombat = true;
        if (changed)
            ResetPiles("draw", "discard", "exhaust");
    }

    private static void DeckButtonInitializePostfix(object player)
    {
        if (player == null || ReferenceEquals(player, _player) && PileScreens.ContainsKey("deck"))
            return;
        _player = player;
        ResetPiles("deck");
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
            return;
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
            page.AddChild(node);
            if (node is Control control)
            {
                control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                control.Visible = true;
            }
            // Its back button closes the top screen's capstone; there is
            // nothing to go back from down here.
            if (node.GetNodeOrNull("BackButton") is CanvasItem back)
                back.Visible = false;
            PileScreens[tab] = node;
            PileBindings[tab] = pile;
            screen.SetPlaceholder(tab, "");
        }
        catch (Exception e)
        {
            DsLog.Info($"could not show {tab} pile: {e.InnerException?.Message ?? e.Message}");
        }
    }

    private static void ResetIfStale(string tab, object pile)
    {
        if (PileScreens.Remove(tab, out var old) && GodotObject.IsInstanceValid(old))
            old.QueueFree();
        PileBindings.Remove(tab);
        if (pile == null)
        {
            DsBottomScreen.Instance?.SetPlaceholder(tab, tab == "deck"
                ? "Your deck shows here once a run starts."
                : $"Your {tab} pile shows here during combat.");
        }
    }
}
