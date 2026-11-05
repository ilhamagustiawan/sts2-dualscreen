using System;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Lifts the launcher's boot backdrop (GodotApp's boot cover and the bottom
/// screen's) once the game has drawn something to replace it: the startup
/// loading screen or the main menu up top, the menu art or a run below.
/// Each side lifts once; the Java side has its own timeout as a backstop.
/// Also tells when the top screen's main menu is up (OnMenuReady).
/// </summary>
internal static class DsBootCover
{
    private const string MainMenuType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string LoadingScreenType = "STS2Mobile.Patches.AndroidStartupLoadingScreen";
    // Frames drawn after the trigger before lifting, so the game's picture is
    // on screen under the cover rather than still on its way.
    private const int SettleFrames = 2;

    private static bool _topLifted;
    private static bool _bottomLifted;

    // The main menu's first appearance, and who waits for it.
    private static bool _menuPatched;
    private static bool _menuReady;
    private static Action _onMenu;

    public static void Apply(Harmony harmony)
    {
        _menuPatched = PatchPostfix(harmony, MainMenuType, "_Ready", nameof(MenuReadyPostfix));
        PatchPostfix(harmony, LoadingScreenType, "EnsureUiBuilt", nameof(GameDrawnPostfix));
    }

    private static bool PatchPostfix(Harmony harmony, string typeName, string method, string postfix)
    {
        try
        {
            var target = AccessTools.TypeByName(typeName) is { } type ? AccessTools.Method(type, method) : null;
            if (target == null)
            {
                DsLog.Warn($"boot cover: {typeName}.{method} not found");
                return false;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(DsBootCover), postfix));
            return true;
        }
        catch (Exception e)
        {
            DsLog.Warn($"boot cover: failed to patch {typeName}.{method}: {e.Message}");
            return false;
        }
    }

    private static void GameDrawnPostfix() => LiftTop();

    private static void MenuReadyPostfix()
    {
        LiftTop();
        if (_menuReady)
        {
            return;
        }

        // Once the menu has drawn, as for the cover: then the waiters run.
        AfterFrames(SettleFrames, () =>
        {
            _menuReady = true;
            var then = _onMenu;
            _onMenu = null;
            then?.Invoke();
        });
    }

    /// <summary>
    /// Runs [then] once the top screen's main menu is up, or now if it is
    /// already, or if the menu could not be patched.
    /// </summary>
    public static void OnMenuReady(Action then)
    {
        if (_menuReady || !_menuPatched)
        {
            then();
            return;
        }
        _onMenu += then;
    }

    /// <summary>The top screen has the game's own picture.</summary>
    public static void LiftTop()
    {
        if (_topLifted)
        {
            return;
        }
        _topLifted = true;
        AfterFrames(SettleFrames, () => Call("liftBootCoverFromGame"));
    }

    /// <summary>The bottom screen has the mod's own picture, or will have none.</summary>
    public static void LiftBottom()
    {
        if (_bottomLifted)
        {
            return;
        }
        _bottomLifted = true;
        AfterFrames(SettleFrames, () => Call("liftBottomCoverFromGame"));
    }

    private static void AfterFrames(int frames, Action then)
    {
        if (frames <= 0 || Engine.GetMainLoop() is not SceneTree tree)
        {
            then();
            return;
        }
        Action step = null;
        step = () =>
        {
            if (--frames > 0)
            {
                return;
            }
            RenderingServer.FramePostDraw -= step;
            then();
        };
        RenderingServer.FramePostDraw += step;
    }

    private static void Call(string method)
    {
        try
        {
            var wrapper = Engine.GetSingleton("JavaClassWrapper");
            var app = wrapper.Call("wrap", "com.godot.game.GodotApp").AsGodotObject();
            var lifted = app?.Call(method);
            DsLog.Info($"boot cover: {method} -> {lifted}");
        }
        catch (Exception e)
        {
            DsLog.Warn($"boot cover: {method} failed: {e.Message}");
        }
    }
}
