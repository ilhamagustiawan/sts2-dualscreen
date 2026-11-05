using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

// The game's own look, for what the bottom screen draws itself: its colours
// (StsColors), icons, hsv shader, localized names and click sound, all looked
// up by name. Anything missing falls back to a plain look and logs once.
internal static class DsStyle
{
    public static readonly Color Cream = new("FFF6E2");
    public static readonly Color HalfCream = new("FFF6E280");
    public static readonly Color Gold = new("EFC851");
    public static readonly Color Disabled = new(0.5f, 0.5f, 0.5f, 0.75f);
    public static readonly Color Outline = new(0.08f, 0.06f, 0.05f);

    public const string MapIcon = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_map.tres";
    public const string DeckIcon = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_deck.tres";
    public const string DiscardIcon = "res://images/packed/combat_ui/discard_pile.png";
    private const string HsvShader = "res://shaders/hsv.gdshader";
    private const string ClickSfx = "event:/sfx/ui/clicks/ui_click";
    // What the game plays as it opens these screens: its map
    // (NMapScreen.Open) and a card pile (NCardPileScreen.ShowScreen, one of
    // its placeholder sounds, played by NDebugAudioManager).
    private const string MapOpenSfx = "event:/sfx/ui/map/map_open";
    private const string PileOpenSound = "map_open.mp3";

    private static readonly Dictionary<string, Texture2D> Icons = new();
    private static readonly HashSet<string> Logged = new();
    private static Shader _hsv;
    private static bool _hsvLooked;

    public static Texture2D Icon(string path)
    {
        if (Icons.TryGetValue(path, out var cached))
            return cached;
        Texture2D texture = null;
        try
        {
            if (ResourceLoader.Exists(path))
                texture = ResourceLoader.Load<Texture2D>(path);
        }
        catch (Exception e)
        {
            DsLog.Warn($"icon {path} failed: {e.Message}");
        }
        Icons[path] = texture;
        if (texture != null)
            DsLog.Info($"icon {path}");
        else
            DsLog.Warn($"icon {path} not found; text only");
        return texture;
    }

    /// <summary>A fresh material on the game's hsv shader (params s and v), or null.</summary>
    public static ShaderMaterial Hsv()
    {
        if (!_hsvLooked)
        {
            _hsvLooked = true;
            try
            {
                _hsv = ResourceLoader.Exists(HsvShader) ? ResourceLoader.Load<Shader>(HsvShader) : null;
            }
            catch (Exception e)
            {
                DsLog.Warn($"hsv shader failed: {e.Message}");
            }
            if (_hsv != null)
                DsLog.Info("hsv shader found");
            else
                DsLog.Warn("hsv shader not found; tabs dim by modulate");
        }
        if (_hsv == null)
            return null;
        var material = new ShaderMaterial { Shader = _hsv };
        material.SetShaderParameter("h", 0f);
        material.SetShaderParameter("s", 1f);
        material.SetShaderParameter("v", 1f);
        return material;
    }

    // ── localized text ─────────────────────────────────────────────────────

    private static Type _locType;
    private static ConstructorInfo _locNew;
    private static MethodInfo _locExists, _locAdd, _locFormat;
    private static readonly Regex Markup = new(@"\[/?[a-zA-Z_]+[^\]]*\]");
    private static readonly Regex EmptyBrackets = new(@"\(\s*\)|\[\s*\]|【\s*】|（\s*）");
    private static readonly Regex Spaces = new(@"\s{2,}");

    /// <summary>
    /// A game string (a pile or screen title) with its {Hotkey} left empty and
    /// any markup or empty brackets removed. The fallback if it can't be had,
    /// as before the game's localization is up.
    /// </summary>
    public static string Loc(string table, string key, string fallback)
    {
        string text = null;
        try
        {
            if (_locType == null)
            {
                _locType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Localization.LocString");
                if (_locType != null)
                {
                    _locNew = _locType.GetConstructor(new[] { typeof(string), typeof(string) });
                    _locExists = _locType.GetMethod("Exists", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    _locAdd = _locType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null);
                    _locFormat = _locType.GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }
            }
            if (_locNew != null && _locFormat != null)
            {
                var loc = _locNew.Invoke(new object[] { table, key });
                if (_locExists?.Invoke(loc, null) is not false)
                {
                    _locAdd?.Invoke(loc, new object[] { "Hotkey", "" });
                    text = _locFormat.Invoke(loc, null) as string;
                }
            }
        }
        catch (Exception)
        {
            text = null;
        }
        if (text != null)
        {
            text = Markup.Replace(text, "");
            text = EmptyBrackets.Replace(text, "");
            text = Spaces.Replace(text, " ").Trim();
        }
        var result = string.IsNullOrEmpty(text) ? fallback : text;
        if (Logged.Add($"{table}/{key}={result}"))
            DsLog.Info($"text {table}/{key}: \"{result}\"{(result == fallback && text != fallback ? " (fallback)" : "")}");
        return result;
    }

    // ── sound ──────────────────────────────────────────────────────────────

    private static MethodInfo _sfxPlay;
    private static bool _sfxLooked;
    private static PropertyInfo _debugAudio;
    private static MethodInfo _debugPlay;
    private static object _noPitchVariance;
    private static bool _debugAudioLooked;

    /// <summary>The game's button click.</summary>
    public static void Click() => Sfx(ClickSfx);

    /// <summary>The sound the game makes opening this tab's screen: the map, or a card pile.</summary>
    public static void OpenSound(string tab)
    {
        if (tab == "map")
            Sfx(MapOpenSfx);
        else
            DebugSound(PileOpenSound);
    }

    // A sound event, through the game's SfxCmd (its volume settings apply).
    private static void Sfx(string path)
    {
        try
        {
            if (!_sfxLooked)
            {
                _sfxLooked = true;
                var sfx = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.SfxCmd");
                _sfxPlay = sfx == null ? null : AccessTools.Method(sfx, "Play", new[] { typeof(string), typeof(float) });
                if (_sfxPlay != null)
                    DsLog.Info("ui sounds found");
                else
                    DsLog.Warn("ui sounds not found; tabs are silent");
            }
            _sfxPlay?.Invoke(null, new object[] { path, 1f });
        }
        catch (Exception e)
        {
            _sfxPlay = null;
            DsLog.Warn($"ui sound failed, tabs are silent: {e.InnerException?.Message ?? e.Message}");
        }
    }

    // One of the game's placeholder sounds (res://debug_audio), played as it
    // plays them, through NGame's NDebugAudioManager.
    private static void DebugSound(string name)
    {
        try
        {
            if (!_debugAudioLooked)
            {
                _debugAudioLooked = true;
                var manager = AccessTools.TypeByName("MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager");
                _debugAudio = manager == null ? null : AccessTools.Property(manager, "Instance");
                _debugPlay = manager == null ? null : AccessTools.Method(manager, "Play");
                var variance = _debugPlay?.GetParameters() is { Length: 3 } parameters ? parameters[2].ParameterType : null;
                _noPitchVariance = variance is { IsEnum: true } ? Enum.ToObject(variance, 0) : null;
                if (_debugAudio == null || _debugPlay == null || _noPitchVariance == null)
                {
                    _debugPlay = null;
                    DsLog.Warn("game placeholder sounds not found; pile tabs open silently");
                }
            }
            if (_debugPlay != null && _debugAudio.GetValue(null) is { } instance)
                _debugPlay.Invoke(instance, new[] { name, 1f, _noPitchVariance });
        }
        catch (Exception e)
        {
            _debugPlay = null;
            DsLog.Warn($"placeholder sound failed, pile tabs open silently: {e.InnerException?.Message ?? e.Message}");
        }
    }

    // ── motion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A tween on owner, with the part of the bottom screen it is on captured
    /// at full rate for as long as it runs. Every one-shot animation here
    /// starts with this.
    /// </summary>
    public static Tween Animate(Node owner, double seconds)
    {
        DsBottomScreen.Instance?.MarkChanged(owner, seconds);
        DsPerf.NoteTween();
        return owner.CreateTween();
    }

    /// <summary>A tween on owner that repeats forever; its part captures at the ambient rate.</summary>
    public static Tween Loop(Node owner)
    {
        DsBottomScreen.Instance?.MarkChanged(owner, 0.1);
        DsPerf.NoteTween();
        return owner.CreateTween().SetLoops(0);
    }

    public static void Kill(ref Tween tween)
    {
        if (tween != null && GodotObject.IsInstanceValid(tween))
            tween.Kill();
        tween = null;
    }

    public static bool Running(Tween tween) => tween != null && GodotObject.IsInstanceValid(tween) && tween.IsRunning();

    /// <summary>Text in the game's font, outlined like its labels; colour comes from Modulate.</summary>
    public static Label Text(int size, int outline)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Outline);
        label.AddThemeConstantOverride("outline_size", outline);
        return label;
    }
}
