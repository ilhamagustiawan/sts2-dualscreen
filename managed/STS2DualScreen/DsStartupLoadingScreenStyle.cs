using System;
using Godot;
using HarmonyLib;

namespace STS2DualScreen;

/// <summary>
/// Restyles the compat pack's startup loading screen to match the launcher's
/// visual identity: dusk violet-to-teal sky gradient, warm horizon glow,
/// gold type and cards panel progress bar.
/// </summary>
internal static class DsStartupLoadingScreenStyle
{
    private static readonly StringName StyledMeta = new("ds_styled");
    private const string BoldFontPath = "res://themes/kreon_bold_shared.tres";
    private const string RegularFontPath = "res://themes/kreon_regular_shared.tres";

    public static void Apply(Harmony harmony)
    {
        var screenType = AccessTools.TypeByName("STS2Mobile.Patches.AndroidStartupLoadingScreen");
        var ensureUi = screenType == null ? null : AccessTools.Method(screenType, "EnsureUiBuilt");

        if (ensureUi == null)
        {
            DsLog.Warn("startup style: AndroidStartupLoadingScreen.EnsureUiBuilt not found; keeping original style");
            return;
        }

        try
        {
            harmony.Patch(
                ensureUi,
                postfix: new HarmonyMethod(typeof(DsStartupLoadingScreenStyle), nameof(EnsureUiBuiltPostfix)));
        }
        catch (Exception ex)
        {
            DsLog.Warn($"startup style: failed to patch EnsureUiBuilt: {ex.Message}");
        }
    }

    [HarmonyPostfix]
    private static void EnsureUiBuiltPostfix(object __instance)
    {
        if (__instance is not Control screen)
            return;

        try
        {
            if (screen.HasMeta(StyledMeta))
                return;
            screen.SetMeta(StyledMeta, true);

            Restyle(screen);
        }
        catch (Exception ex)
        {
            DsLog.Warn($"startup style: error applying theme: {ex.Message}");
        }
    }

    private static void Restyle(Control screen)
    {
        // 1. Hide the old flat background and the thick orange/blue edge accents
        if (screen.GetNodeOrNull<ColorRect>("Background") is { } oldBg)
            oldBg.Visible = false;
        if (screen.GetNodeOrNull<ColorRect>("TopAccent") is { } topAccent)
            topAccent.Visible = false;
        if (screen.GetNodeOrNull<ColorRect>("BottomAccent") is { } bottomAccent)
            bottomAccent.Visible = false;

        // 2. Dusk sky background gradient: violet (#272238) -> indigo (#363E54) -> teal-grey (#3C5A64)
        var gradientBg = new TextureRect
        {
            Name = "DsDuskBackground",
            LayoutMode = 1,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient
                {
                    Colors = new[]
                    {
                        new Color("#272238"),
                        new Color("#363E54"),
                        new Color("#3C5A64")
                    },
                    Offsets = new[] { 0f, 0.5f, 1f }
                },
                FillFrom = Vector2.Zero,
                FillTo = Vector2.Down,
                Width = 16,
                Height = 128
            }
        };
        screen.AddChild(gradientBg);
        screen.MoveChild(gradientBg, 0);

        // 3. Warm horizon glow at the bottom edge
        var glow = new TextureRect
        {
            Name = "DsDuskGlow",
            LayoutMode = 1,
            AnchorTop = 1f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetTop = -240,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient
                {
                    Colors = new[]
                    {
                        new Color(0.93f, 0.54f, 0.17f, 0f),
                        new Color(0.93f, 0.54f, 0.17f, 0.22f)
                    },
                    Offsets = new[] { 0f, 1f }
                },
                FillFrom = Vector2.Zero,
                FillTo = Vector2.Down,
                Width = 16,
                Height = 64
            }
        };
        screen.AddChild(glow);
        screen.MoveChild(glow, 1);

        // 4. Subtle gold rim line at top edge
        var rim = new ColorRect
        {
            Name = "DsGoldRim",
            LayoutMode = 1,
            AnchorRight = 1f,
            OffsetBottom = 2,
            Color = new Color("#F7C62F80"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        screen.AddChild(rim);

        // 5. Fonts (if packaged in port_compat.pck)
        Font boldFont = null;
        Font regularFont = null;
        try
        {
            if (ResourceLoader.Exists(BoldFontPath))
                boldFont = ResourceLoader.Load<Font>(BoldFontPath);
            if (ResourceLoader.Exists(RegularFontPath))
                regularFont = ResourceLoader.Load<Font>(RegularFontPath);
        }
        catch { }

        // 6. TitleLabel: Gold with dark outline and shadow
        if (screen.GetNodeOrNull<Label>("CenterContainer/VBox/TitleLabel") is { } title)
        {
            if (boldFont != null)
                title.AddThemeFontOverride("font", boldFont);
            title.AddThemeFontSizeOverride("font_size", 42);
            title.AddThemeColorOverride("font_color", new Color("#F7C62F"));
            title.AddThemeColorOverride("font_shadow_color", new Color("#5A2A06"));
            title.AddThemeConstantOverride("shadow_offset_x", 2);
            title.AddThemeConstantOverride("shadow_offset_y", 2);
            title.AddThemeColorOverride("font_outline_color", new Color("#21182A"));
            title.AddThemeConstantOverride("outline_size", 4);
        }

        // 7. DetailsLabel: Warm cream text
        if (screen.GetNodeOrNull<Label>("CenterContainer/VBox/DetailsLabel") is { } details)
        {
            if (regularFont != null)
                details.AddThemeFontOverride("font", regularFont);
            details.AddThemeFontSizeOverride("font_size", 22);
            details.AddThemeColorOverride("font_color", new Color("#F4EEE0E6"));
            details.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
            details.AddThemeConstantOverride("shadow_offset_x", 1);
            details.AddThemeConstantOverride("shadow_offset_y", 1);
        }

        // 8. HintLabel: Muted cream with informative message based on cache state
        if (screen.GetNodeOrNull<Label>("CenterContainer/VBox/HintLabel") is { } hint)
        {
            if (regularFont != null)
                hint.AddThemeFontOverride("font", regularFont);
            hint.Modulate = Colors.White;
            hint.AddThemeFontSizeOverride("font_size", 16);
            hint.AddThemeColorOverride("font_color", new Color("#F4EEE080"));
            hint.Text = DsWarmupCache.IsHit
                ? "Combat effects already prepared"
                : "First launch: preparing combat effects. This happens once.";
        }

        // 9. Progress bar: Card fill panel with gold border and gold fill
        if (screen.GetNodeOrNull<PanelContainer>("CenterContainer/VBox/ProgressOuter") is { } progressOuter)
        {
            var panelStyle = new StyleBoxFlat
            {
                BgColor = new Color("#120F1AE0"),
                BorderColor = new Color("#F7C62F60"),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ShadowColor = new Color(0f, 0f, 0f, 0.45f),
                ShadowSize = 4,
                ShadowOffset = new Vector2(0, 2)
            };
            progressOuter.AddThemeStyleboxOverride("panel", panelStyle);
        }

        if (screen.GetNodeOrNull<ColorRect>("CenterContainer/VBox/ProgressOuter/ProgressMargin/ProgressFrame") is { } frame)
        {
            frame.Color = new Color(0.08f, 0.07f, 0.12f, 0.85f);
        }

        if (screen.GetNodeOrNull<ColorRect>("CenterContainer/VBox/ProgressOuter/ProgressMargin/ProgressFrame/ProgressFill") is { } fill)
        {
            fill.Color = new Color("#F2C94C");
        }
    }
}
