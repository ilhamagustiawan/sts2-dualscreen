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
/// The top cover can be held past the menu (HoldTop) while the startup
/// preload runs; its "Loading..." line then names each step.
/// </summary>
internal static class DsBootCover
{
    private const string MainMenuType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string LoadingScreenType = "STS2Mobile.Patches.AndroidStartupLoadingScreen";
    // Frames drawn after the trigger before lifting, so the game's picture is
    // on screen under the cover rather than still on its way.
    private const int SettleFrames = 2;
    // How often a held cover's line follows the step (its counters change every frame).
    private const ulong StatusIntervalMs = 200;

    private static bool _topLifted;
    private static bool _topHeld;
    private static bool _topGone;
    private static string _status;
    private static int _percent = -1;
    private static ulong _nextStatusMs;
    private static GodotObject _app;
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
        AfterFrames(SettleFrames, SendLiftTop);
    }

    /// <summary>Keeps the top cover up past the main menu, until ReleaseTop.</summary>
    public static void HoldTop()
    {
        if (_topHeld || _topGone || Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }
        _topHeld = true;
        tree.ProcessFrame += PumpStatus;
    }

    /// <summary>Ends a hold and lifts the top cover. Game thread only.</summary>
    public static void ReleaseTop()
    {
        if (!_topHeld)
        {
            return;
        }
        _topHeld = false;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame -= PumpStatus;
        }
        // A full bar as the cover fades.
        Call("setBootCoverStatus", _status ?? "", 1.0);
        AfterFrames(SettleFrames, SendLiftTop);
    }

    private static void SendLiftTop()
    {
        if (_topHeld || _topGone)
        {
            return;
        }
        _topGone = true;
        Call("liftBootCoverFromGame");
    }

    // The held cover's line follows DsLoadingDetail; "" brings back "Loading...".
    private static void PumpStatus()
    {
        var now = Time.GetTicksMsec();
        if (now < _nextStatusMs)
        {
            return;
        }
        _nextStatusMs = now + StatusIntervalMs;

        // Whole percents: the bar eases between them on its own.
        var text = DsLoadingDetail.Text;
        var progress = DsLoadingDetail.Progress;
        var percent = progress < 0f ? -1 : (int)(progress * 100f);
        if (text == _status && percent == _percent)
        {
            return;
        }
        _status = text;
        _percent = percent;
        Call("setBootCoverStatus", text, percent < 0 ? DsLoadingDetail.Unknown : percent / 100.0);
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

    private static void Call(string method, params Variant[] args)
    {
        try
        {
            _app ??= Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp").AsGodotObject();
            var lifted = _app?.Call(method, args);
            DsLog.Debug($"boot cover: {method} -> {lifted}");
        }
        catch (Exception e)
        {
            DsLog.Warn($"boot cover: {method} failed: {e.Message}");
        }
    }
}
