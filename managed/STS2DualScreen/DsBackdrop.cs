using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Renders the game's main-menu art (without its logo) offscreen once per
/// game build and saves it for the launcher's screens and boot covers
/// (Backdrop.kt). No game art ships in the APK; this is the player's copy.
/// Waits for the Common preload, so it doesn't compete with it.
/// </summary>
internal static class DsBackdrop
{
    // Read by the launcher's Backdrop.kt; keep the names in step.
    private const string FileName = "backdrop.jpg";
    private const string LogoName = "backdrop_logo.png";
    private const string StampName = "backdrop.stamp";

    // Bump to reshoot on builds already shot (e.g. when the logo was added).
    private const string StampVersion = "3";

    private const string MainMenuType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string ArtScene = "res://scenes/backgrounds/main_menu_bg.tscn";
    private const string LogoPath = "BgContainer/Control/Logo";
    private static readonly Vector2I ArtSize = new(1920, 1080);

    // Frames the art animates before the shot, so it is past any fade-in.
    private const int SettleFrames = 30;
    private const float JpgQuality = 0.9f;

    private static bool _started;
    private static SceneTree _tree;
    private static SubViewport _viewport;
    private static int _frames;
    private static CanvasItem _art;
    private static CanvasItem _logo;
    private static Image _backdrop;

    // What the viewport shows: the art without its logo, then the logo alone.
    private enum Shot { Art, Logo }
    private static Shot _shot;

    public static void Apply(Harmony harmony)
    {
        var ready = AccessTools.TypeByName(MainMenuType) is { } type ? AccessTools.Method(type, "_Ready") : null;
        if (ready == null)
        {
            DsLog.Warn("backdrop: NMainMenu._Ready not found");
            return;
        }
        harmony.Patch(ready, postfix: new HarmonyMethod(typeof(DsBackdrop), nameof(MainMenuReadyPostfix)));
    }

    private static void MainMenuReadyPostfix()
    {
        if (_started)
        {
            return;
        }
        _started = true;

        if (IsCurrent())
            return;
        DsStartup.CommonPreloadTask.ContinueWith(_ => Callable.From(Begin).CallDeferred());
    }

    // The shot on disk was taken from this game build.
    private static bool IsCurrent()
    {
        try
        {
            var stamp = Path.Combine(DsPaths.FilesDir, StampName);
            return File.Exists(Path.Combine(DsPaths.FilesDir, FileName))
                && File.Exists(Path.Combine(DsPaths.FilesDir, LogoName))
                && File.Exists(stamp)
                && File.ReadAllText(stamp) == Build();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Build() => $"{DsEntry.Config?.Build ?? ""}:{StampVersion}";

    private static void Begin()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }
        if (!ResourceLoader.Exists(ArtScene) || ResourceLoader.LoadThreadedRequest(ArtScene) != Error.Ok)
        {
            DsLog.Warn("backdrop: menu art not available");
            return;
        }
        _tree = tree;
        _tree.ProcessFrame += Step;
    }

    // Load, stage the art, let it animate, shoot it; then the logo alone, shoot that.
    private static void Step()
    {
        try
        {
            if (_viewport == null)
            {
                Stage();
                return;
            }
            if (++_frames < SettleFrames)
            {
                return;
            }
            if (_shot == Shot.Art)
            {
                ShootArt();
                return;
            }
            ShootLogo();
        }
        catch (Exception e)
        {
            DsLog.Warn($"backdrop: failed: {e.Message}");
            Finish();
        }
    }

    private static void Stage()
    {
        var status = ResourceLoader.LoadThreadedGetStatus(ArtScene);
        if (status == ResourceLoader.ThreadLoadStatus.InProgress)
        {
            return;
        }
        if (status != ResourceLoader.ThreadLoadStatus.Loaded || ResourceLoader.LoadThreadedGet(ArtScene) is not PackedScene scene)
        {
            DsLog.Warn($"backdrop: menu art failed to load ({status})");
            Finish();
            return;
        }

        // Offscreen: a SubViewport outside a container renders but isn't shown.
        var art = scene.Instantiate<Control>();
        _art = art;
        _logo = art.GetNodeOrNull<CanvasItem>(LogoPath);
        if (_logo != null)
        {
            _logo.Visible = false;
        }
        _viewport = new SubViewport
        {
            Name = "DsBackdrop",
            Size = ArtSize,
            Disable3D = true,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _viewport.AddChild(art);
        art.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _tree.Root.AddChild(_viewport);
    }

    private static void ShootArt()
    {
        _backdrop = _viewport.GetTexture().GetImage();
        if (_logo == null)
        {
            DsLog.Debug("backdrop: no logo node; art only");
            Save(null);
            return;
        }

        // The logo alone, on transparency: everything off but the logo's own line.
        _logo.Visible = true;
        _logo.Modulate = Colors.White;
        _logo.SelfModulate = Colors.White;
        for (Node node = _logo; node != _art && node.GetParent() is { } parent; node = parent)
        {
            foreach (var sibling in parent.GetChildren())
            {
                if (sibling != node && sibling is CanvasItem item)
                {
                    item.Visible = false;
                }
            }
            if (parent is CanvasItem holder)
            {
                holder.Visible = true;
                holder.Modulate = Colors.White;
            }
        }
        // Its glow particles blend additively: over transparency, a grey box.
        foreach (var child in _logo.GetChildren())
        {
            if (child is CpuParticles2D or GpuParticles2D)
            {
                ((CanvasItem)child).Visible = false;
            }
        }

        _shot = Shot.Logo;
        _frames = 0;
    }

    private static void ShootLogo()
    {
        var logo = _viewport.GetTexture().GetImage();
        var used = logo.GetUsedRect();
        Save(used.HasArea() ? logo.GetRegion(used) : null);
    }

    // Encoding is slow; off the main thread. Rename so readers never see half a file.
    private static void Save(Image logo)
    {
        var art = _backdrop;
        Finish();

        var dir = DsPaths.FilesDir;
        var build = Build();
        Task.Run(() =>
        {
            try
            {
                if (!Write(art, Path.Combine(dir, FileName), Format.Jpg))
                {
                    return;
                }
                if (logo != null && !Write(logo, Path.Combine(dir, LogoName), Format.Png))
                {
                    return;
                }
                File.WriteAllText(Path.Combine(dir, StampName), build);
                DsLog.Debug($"backdrop: saved {art.GetWidth()}x{art.GetHeight()}, logo {(logo == null ? "none" : $"{logo.GetWidth()}x{logo.GetHeight()}")}");
            }
            catch (Exception e)
            {
                DsLog.Warn($"backdrop: save failed: {e.Message}");
            }
        });
    }

    private enum Format { Jpg, Png }

    private static bool Write(Image image, string path, Format format)
    {
        var temp = path + ".tmp";
        var error = format == Format.Jpg ? image.SaveJpg(temp, JpgQuality) : image.SavePng(temp);
        if (error != Error.Ok)
        {
            DsLog.Warn($"backdrop: saving {Path.GetFileName(path)} failed ({error})");
            return false;
        }
        File.Move(temp, path, overwrite: true);
        return true;
    }

    private static void Finish()
    {
        if (_tree != null)
        {
            _tree.ProcessFrame -= Step;
        }
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
        {
            _viewport.QueueFree();
        }
        _viewport = null;
        _art = null;
        _logo = null;
        _backdrop = null;
    }
}
