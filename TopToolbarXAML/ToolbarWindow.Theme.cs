// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Controls;
using TopToolbar.Models;
using Windows.UI;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private ToolbarTheme? _lastAccentTheme;
        private bool _hasAccentPair;
        private Color _accentA;
        private Color _accentB;

        private void ApplyTheme(ToolbarTheme theme)
        {
            if (RootGrid?.Resources == null)
            {
                return;
            }

            var tokens = GetThemeTokens(theme);
            ApplySaturationProfile(theme, tokens);
            EnsureInteractiveContrast(tokens);
            EnsureAccentPair(theme, tokens);
            var iconColor = GetNeutralIconColor(tokens);

            var glowStart = BlendRgb(tokens.BackgroundInner, _accentA, tokens.RandomInnerBlend);
            var glowMid = BlendRgb(tokens.BackgroundMiddle, _accentA, tokens.RandomMiddleBlend);
            var glowOuter = BlendRgb(tokens.BackgroundMiddle, _accentB, tokens.RandomOuterBlend);

            var background = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 1.02),
                GradientOrigin = new Windows.Foundation.Point(0.5, 1.02),
                RadiusX = 0.95,
                RadiusY = 1.25,
            };
            background.GradientStops.Add(new GradientStop { Color = glowStart, Offset = 0.0 });
            background.GradientStops.Add(new GradientStop { Color = glowMid, Offset = 0.30 });
            background.GradientStops.Add(new GradientStop { Color = glowOuter, Offset = 0.62 });
            background.GradientStops.Add(new GradientStop { Color = tokens.BackgroundOuter, Offset = 1.0 });

            SetResource("ToolbarBackgroundBrush", background);
            SetResource("ToolbarBorderBrush", new SolidColorBrush(tokens.Border));
            SetResource("ToolbarLabelBrush", new SolidColorBrush(tokens.Label));
            SetResource("ToolbarIconColor", iconColor);
            SetResource("ToolbarIconBrush", new SolidColorBrush(iconColor));
            SetResource("ToolbarSeparatorBrush", new SolidColorBrush(tokens.Separator));
            SetResource("ToolbarNotificationAccentBrush", new SolidColorBrush(tokens.NotificationAccent));
            SetResource("ToolbarButtonHoverBrush", new SolidColorBrush(tokens.ButtonHover));
            SetResource("ToolbarButtonPressedBrush", new SolidColorBrush(tokens.ButtonPressed));
            SetResource("ToolbarButtonDisabledBrush", new SolidColorBrush(tokens.ButtonDisabled));
            SetResource("ButtonBackground", new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)));
            SetResource("ButtonBackgroundPointerOver", new SolidColorBrush(tokens.ButtonHover));
            SetResource("ButtonBackgroundPressed", new SolidColorBrush(tokens.ButtonPressed));
            SetResource("ButtonBackgroundDisabled", new SolidColorBrush(tokens.ButtonDisabled));
            SetResource("ToolbarTextFontFamily", new FontFamily(tokens.FontFamily));

            // Update key visual references immediately.
            if (ToolbarContainer != null)
            {
                ToolbarContainer.Background = background;
                ToolbarContainer.BorderBrush = new SolidColorBrush(tokens.Border);
            }

            if (SettingsSeparator != null)
            {
                SettingsSeparator.Background = new SolidColorBrush(tokens.Separator);
            }

            ApplyThemeToLiveToolbarElements(tokens, iconColor);
            SyncToastWindowTheme();
        }

        private void EnsureAccentPair(ToolbarTheme theme, ThemeTokens tokens)
        {
            if (_hasAccentPair && _lastAccentTheme == theme)
            {
                return;
            }

            _accentA = JitterColor(tokens.HighlightA, tokens.HighlightJitter);
            _accentB = JitterColor(tokens.HighlightB, tokens.HighlightJitter);
            _lastAccentTheme = theme;
            _hasAccentPair = true;
        }

        private void SetResource(string key, object value)
        {
            if (RootGrid.Resources.ContainsKey(key))
            {
                RootGrid.Resources[key] = value;
            }
            else
            {
                RootGrid.Resources.Add(key, value);
            }
        }

        private void ApplyThemeToLiveToolbarElements(ThemeTokens tokens, Color iconColor)
        {
            if (ToolbarContainer == null)
            {
                return;
            }

            var labelBrush = new SolidColorBrush(tokens.Label);
            var iconBrush = new SolidColorBrush(iconColor);
            var hoverBrush = new SolidColorBrush(tokens.ButtonHover);
            var pressedBrush = new SolidColorBrush(tokens.ButtonPressed);
            var disabledBrush = new SolidColorBrush(tokens.ButtonDisabled);

            foreach (var presenter in FindDescendants<ToolbarIconPresenter>(ToolbarContainer))
            {
                presenter.Foreground = iconColor;
            }

            foreach (var text in FindDescendants<TextBlock>(ToolbarContainer))
            {
                text.Foreground = labelBrush;
            }

            foreach (var fontIcon in FindDescendants<FontIcon>(ToolbarContainer))
            {
                fontIcon.Foreground = iconBrush;
            }

            foreach (var ring in FindDescendants<ProgressRing>(ToolbarContainer))
            {
                ring.Foreground = iconBrush;
            }

            foreach (var button in FindDescendants<Button>(ToolbarContainer))
            {
                button.Resources["ButtonBackgroundPointerOver"] = hoverBrush;
                button.Resources["ButtonBackgroundPressed"] = pressedBrush;
                button.Resources["ButtonBackgroundDisabled"] = disabledBrush;
            }
        }

        private static Color GetNeutralIconColor(ThemeTokens tokens)
        {
            var luminance = RelativeLuminance(tokens.BackgroundMiddle);
            if (luminance >= 0.62)
            {
                // Light backgrounds -> near-black icons
                return Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
            }

            if (luminance <= 0.36)
            {
                // Dark backgrounds -> near-white icons
                return Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2);
            }

            // Mid-tone backgrounds -> neutral gray icons
            return Color.FromArgb(0xFF, 0x88, 0x88, 0x88);
        }

        private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var nested in FindDescendants<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private static ThemeTokens GetThemeTokens(ToolbarTheme theme)
        {
            return theme switch
            {
                ToolbarTheme.ArcticGlass => new ThemeTokens
                {
                    BackgroundInner = Hex("E9F6FF", 0xF0),
                    BackgroundMiddle = Hex("D7ECFF", 0xE4),
                    BackgroundOuter = Hex("C5E2FF", 0xD8),
                    Border = Hex("FFFFFF", 0x90),
                    Label = Hex("182D4C"),
                    Icon = Hex("182D4C"),
                    Separator = Hex("1E304A", 0x40),
                    ButtonHover = Hex("457B9D", 0x58),
                    ButtonPressed = Hex("457B9D", 0x86),
                    ButtonDisabled = Hex("1E304A", 0x1A),
                    NotificationAccent = Hex("457B9D"),
                    HighlightA = Hex("A8DADC"),
                    HighlightB = Hex("E63946"),
                    FontFamily = "Segoe UI",
                    HighlightJitter = 18,
                    RandomInnerBlend = 0.24,
                    RandomMiddleBlend = 0.19,
                    RandomOuterBlend = 0.16,
                },

                ToolbarTheme.SunrisePaper => new ThemeTokens
                {
                    BackgroundInner = Hex("FFE2D1", 0xF2),
                    BackgroundMiddle = Hex("FFBC9A", 0xE8),
                    BackgroundOuter = Hex("F2967A", 0xDD),
                    Border = Hex("FFFAF0", 0x95),
                    Label = Hex("4D1F1A"),
                    Icon = Hex("4D1F1A"),
                    Separator = Hex("5A221A", 0x4A),
                    ButtonHover = Hex("F4A261", 0x5E),
                    ButtonPressed = Hex("E76F51", 0x8C),
                    ButtonDisabled = Hex("5A221A", 0x16),
                    NotificationAccent = Hex("E76F51"),
                    HighlightA = Hex("E9C46A"),
                    HighlightB = Hex("E76F51"),
                    FontFamily = "Segoe UI",
                    HighlightJitter = 16,
                    RandomInnerBlend = 0.26,
                    RandomMiddleBlend = 0.22,
                    RandomOuterBlend = 0.19,
                },

                ToolbarTheme.ModernSaaS => new ThemeTokens
                {
                    BackgroundInner = Hex("F1FAEE", 0xEE),
                    BackgroundMiddle = Hex("A8DADC", 0xE2),
                    BackgroundOuter = Hex("1D3557", 0xF2),
                    Border = Hex("A8DADC", 0x8A),
                    Label = Hex("F1FAEE"),
                    Icon = Hex("F1FAEE"),
                    Separator = Hex("A8DADC", 0x4A),
                    ButtonHover = Hex("457B9D", 0x78),
                    ButtonPressed = Hex("E63946", 0x92),
                    ButtonDisabled = Hex("A8DADC", 0x26),
                    NotificationAccent = Hex("E63946"),
                    HighlightA = Hex("E63946"),
                    HighlightB = Hex("A8DADC"),
                    FontFamily = "Segoe UI Variable Text",
                    HighlightJitter = 20,
                    RandomInnerBlend = 0.30,
                    RandomMiddleBlend = 0.24,
                    RandomOuterBlend = 0.20,
                },

                ToolbarTheme.FintechInnovator => new ThemeTokens
                {
                    BackgroundInner = Hex("E9C46A", 0xEE),
                    BackgroundMiddle = Hex("2A9D8F", 0xD8),
                    BackgroundOuter = Hex("264653", 0xF2),
                    Border = Hex("E9C46A", 0x7A),
                    Label = Hex("F1FAEE"),
                    Icon = Hex("E9C46A"),
                    Separator = Hex("E9C46A", 0x56),
                    ButtonHover = Hex("2A9D8F", 0x84),
                    ButtonPressed = Hex("E76F51", 0x94),
                    ButtonDisabled = Hex("264653", 0x22),
                    NotificationAccent = Hex("F4A261"),
                    HighlightA = Hex("F4A261"),
                    HighlightB = Hex("2A9D8F"),
                    FontFamily = "Bahnschrift",
                    HighlightJitter = 20,
                    RandomInnerBlend = 0.31,
                    RandomMiddleBlend = 0.24,
                    RandomOuterBlend = 0.20,
                },

                ToolbarTheme.B2BSolutions => new ThemeTokens
                {
                    BackgroundInner = Hex("EEF0F2", 0xEE),
                    BackgroundMiddle = Hex("98C1D9", 0xDC),
                    BackgroundOuter = Hex("293241", 0xF2),
                    Border = Hex("98C1D9", 0x88),
                    Label = Hex("EEF0F2"),
                    Icon = Hex("E0FBFC"),
                    Separator = Hex("E0FBFC", 0x58),
                    ButtonHover = Hex("3D5A80", 0x82),
                    ButtonPressed = Hex("3D5A80", 0xA8),
                    ButtonDisabled = Hex("98C1D9", 0x26),
                    NotificationAccent = Hex("98C1D9"),
                    HighlightA = Hex("3D5A80"),
                    HighlightB = Hex("E0FBFC"),
                    FontFamily = "Segoe UI",
                    HighlightJitter = 16,
                    RandomInnerBlend = 0.24,
                    RandomMiddleBlend = 0.20,
                    RandomOuterBlend = 0.17,
                },

                ToolbarTheme.SeriousTech => new ThemeTokens
                {
                    BackgroundInner = Hex("DEE2E6", 0xEC),
                    BackgroundMiddle = Hex("495057", 0xD8),
                    BackgroundOuter = Hex("212529", 0xF2),
                    Border = Hex("495057", 0x90),
                    Label = Hex("DEE2E6"),
                    Icon = Hex("007BFF"),
                    Separator = Hex("ADB5BD", 0x4C),
                    ButtonHover = Hex("007BFF", 0x74),
                    ButtonPressed = Hex("007BFF", 0xA0),
                    ButtonDisabled = Hex("ADB5BD", 0x28),
                    NotificationAccent = Hex("007BFF"),
                    HighlightA = Hex("007BFF"),
                    HighlightB = Hex("ADB5BD"),
                    FontFamily = "Segoe UI",
                    HighlightJitter = 14,
                    RandomInnerBlend = 0.22,
                    RandomMiddleBlend = 0.18,
                    RandomOuterBlend = 0.16,
                },

                ToolbarTheme.LegalInsurance => new ThemeTokens
                {
                    BackgroundInner = Hex("EDE7E3", 0xEF),
                    BackgroundMiddle = Hex("8A4F5A", 0xD8),
                    BackgroundOuter = Hex("4C1A22", 0xF2),
                    Border = Hex("B4B8C5", 0x82),
                    Label = Hex("EDE7E3"),
                    Icon = Hex("C5A56F"),
                    Separator = Hex("B4B8C5", 0x4E),
                    ButtonHover = Hex("8A4F5A", 0x88),
                    ButtonPressed = Hex("C5A56F", 0x9A),
                    ButtonDisabled = Hex("B4B8C5", 0x22),
                    NotificationAccent = Hex("C5A56F"),
                    HighlightA = Hex("C5A56F"),
                    HighlightB = Hex("8A4F5A"),
                    FontFamily = "Cambria",
                    HighlightJitter = 14,
                    RandomInnerBlend = 0.26,
                    RandomMiddleBlend = 0.20,
                    RandomOuterBlend = 0.17,
                },

                ToolbarTheme.DigitalProduct => new ThemeTokens
                {
                    BackgroundInner = Hex("F0F0F0", 0xEE),
                    BackgroundMiddle = Hex("04D9FF", 0xCC),
                    BackgroundOuter = Hex("0F0F2F", 0xF2),
                    Border = Hex("04D9FF", 0x7E),
                    Label = Hex("F0F0F0"),
                    Icon = Hex("04D9FF"),
                    Separator = Hex("05F4B7", 0x52),
                    ButtonHover = Hex("02F5E1", 0x72),
                    ButtonPressed = Hex("04D9FF", 0x9E),
                    ButtonDisabled = Hex("F0F0F0", 0x24),
                    NotificationAccent = Hex("05F4B7"),
                    HighlightA = Hex("05F4B7"),
                    HighlightB = Hex("02F5E1"),
                    FontFamily = "Cascadia Code",
                    HighlightJitter = 22,
                    RandomInnerBlend = 0.34,
                    RandomMiddleBlend = 0.28,
                    RandomOuterBlend = 0.23,
                },

                ToolbarTheme.JewelTone => new ThemeTokens
                {
                    BackgroundInner = Hex("E9D8A6", 0xEF),
                    BackgroundMiddle = Hex("0A9396", 0xD2),
                    BackgroundOuter = Hex("001219", 0xF4),
                    Border = Hex("94D2BD", 0x7A),
                    Label = Hex("E9D8A6"),
                    Icon = Hex("94D2BD"),
                    Separator = Hex("94D2BD", 0x52),
                    ButtonHover = Hex("005F73", 0x88),
                    ButtonPressed = Hex("0A9396", 0xA0),
                    ButtonDisabled = Hex("94D2BD", 0x20),
                    NotificationAccent = Hex("E9D8A6"),
                    HighlightA = Hex("0A9396"),
                    HighlightB = Hex("E9D8A6"),
                    FontFamily = "Georgia",
                    HighlightJitter = 16,
                    RandomInnerBlend = 0.24,
                    RandomMiddleBlend = 0.20,
                    RandomOuterBlend = 0.18,
                },

                ToolbarTheme.MinimalCloudMonochrome => new ThemeTokens
                {
                    BackgroundInner = Hex("F9F9F9", 0xFC),
                    BackgroundMiddle = Hex("D9D9D9", 0xE8),
                    BackgroundOuter = Hex("F9F9F9", 0xF2),
                    Border = Hex("D9D9D9", 0xCC),
                    Label = Hex("7C7C7C"),
                    Icon = Hex("7C7C7C"),
                    Separator = Hex("D9D9D9", 0x96),
                    ButtonHover = Hex("B7B7B7", 0x64),
                    ButtonPressed = Hex("7C7C7C", 0x82),
                    ButtonDisabled = Hex("B7B7B7", 0x38),
                    NotificationAccent = Hex("7C7C7C"),
                    HighlightA = Hex("B7B7B7"),
                    HighlightB = Hex("D9D9D9"),
                    FontFamily = "Segoe UI Variable Text",
                    HighlightJitter = 8,
                    RandomInnerBlend = 0.12,
                    RandomMiddleBlend = 0.10,
                    RandomOuterBlend = 0.08,
                },

                _ => new ThemeTokens
                {
                    BackgroundInner = Hex("FCF7F1", 0xEA),
                    BackgroundMiddle = Hex("EEE6DB", 0xDD),
                    BackgroundOuter = Hex("D9CEC0", 0xCF),
                    Border = Hex("FFFFFF", 0x80),
                    Label = Hex("2F3A3F"),
                    Icon = Hex("2F3A3F"),
                    Separator = Hex("2A3439", 0x2F),
                    ButtonHover = Hex("000000", 0x36),
                    ButtonPressed = Hex("000000", 0x58),
                    ButtonDisabled = Hex("000000", 0x12),
                    NotificationAccent = Hex("E63946"),
                    HighlightA = Hex("D8D59A"),
                    HighlightB = Hex("B2D6C3"),
                    FontFamily = "Segoe UI Variable Text",
                    HighlightJitter = 24,
                    RandomInnerBlend = 0.34,
                    RandomMiddleBlend = 0.28,
                    RandomOuterBlend = 0.24,
                },
            };
        }

        private static void ApplySaturationProfile(ToolbarTheme theme, ThemeTokens tokens)
        {
            var reduction = GetSaturationReduction(theme);
            if (reduction <= 0)
            {
                return;
            }

            tokens.BackgroundInner = Desaturate(tokens.BackgroundInner, reduction);
            tokens.BackgroundMiddle = Desaturate(tokens.BackgroundMiddle, reduction);
            tokens.BackgroundOuter = Desaturate(tokens.BackgroundOuter, reduction * 0.8);
            tokens.Border = Desaturate(tokens.Border, reduction);
            tokens.Label = Desaturate(tokens.Label, reduction * 0.55);
            tokens.Icon = Desaturate(tokens.Icon, reduction * 0.55);
            tokens.Separator = Desaturate(tokens.Separator, reduction);
            tokens.ButtonHover = Desaturate(tokens.ButtonHover, reduction);
            tokens.ButtonPressed = Desaturate(tokens.ButtonPressed, reduction);
            tokens.ButtonDisabled = Desaturate(tokens.ButtonDisabled, reduction * 0.9);
            tokens.NotificationAccent = Desaturate(tokens.NotificationAccent, reduction * 0.8);
            tokens.HighlightA = Desaturate(tokens.HighlightA, reduction);
            tokens.HighlightB = Desaturate(tokens.HighlightB, reduction);

            // Tone down random neon shifts as part of lower-saturation profiles.
            tokens.HighlightJitter = Math.Max(6, (int)Math.Round(tokens.HighlightJitter * 0.7));
            tokens.RandomInnerBlend *= 0.76;
            tokens.RandomMiddleBlend *= 0.76;
            tokens.RandomOuterBlend *= 0.76;
        }

        private static double GetSaturationReduction(ToolbarTheme theme)
        {
            return theme switch
            {
                ToolbarTheme.ModernSaaS => 0.22,
                ToolbarTheme.FintechInnovator => 0.27,
                ToolbarTheme.B2BSolutions => 0.16,
                ToolbarTheme.SeriousTech => 0.12,
                ToolbarTheme.LegalInsurance => 0.21,
                ToolbarTheme.DigitalProduct => 0.36,
                ToolbarTheme.JewelTone => 0.24,
                ToolbarTheme.ArcticGlass => 0.12,
                ToolbarTheme.SunrisePaper => 0.13,
                ToolbarTheme.WarmFrosted => 0.08,
                _ => 0.0,
            };
        }

        private static void EnsureInteractiveContrast(ThemeTokens tokens)
        {
            tokens.ButtonHover = EnsureOverlayContrast(tokens.ButtonHover, tokens.BackgroundMiddle, 1.34, 0x70);
            tokens.ButtonPressed = EnsureOverlayContrast(tokens.ButtonPressed, tokens.BackgroundMiddle, 1.62, 0x92);

            var hoverVisible = CompositeOver(tokens.BackgroundMiddle, tokens.ButtonHover);
            var pressedVisible = CompositeOver(tokens.BackgroundMiddle, tokens.ButtonPressed);
            if (ContrastRatio(hoverVisible, pressedVisible) < 1.10)
            {
                var pivot = RelativeLuminance(tokens.BackgroundMiddle) > 0.5
                    ? Color.FromArgb(0xFF, 0x18, 0x18, 0x18)
                    : Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2);
                tokens.ButtonPressed = BlendOverlay(tokens.ButtonPressed, pivot, 0.38, Math.Max(tokens.ButtonPressed.A, (byte)0xA0));
            }
        }

        private static Color EnsureOverlayContrast(Color overlay, Color background, double targetRatio, byte minAlpha)
        {
            var adjusted = overlay.A < minAlpha
                ? Color.FromArgb(minAlpha, overlay.R, overlay.G, overlay.B)
                : overlay;

            if (ContrastRatio(CompositeOver(background, adjusted), background) >= targetRatio)
            {
                return adjusted;
            }

            var darkTarget = Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
            var lightTarget = Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0);

            var darkCandidate = BlendOverlay(adjusted, darkTarget, 0.58, (byte)Math.Max(adjusted.A, minAlpha));
            var lightCandidate = BlendOverlay(adjusted, lightTarget, 0.58, (byte)Math.Max(adjusted.A, minAlpha));

            var darkRatio = ContrastRatio(CompositeOver(background, darkCandidate), background);
            var lightRatio = ContrastRatio(CompositeOver(background, lightCandidate), background);

            var chosen = darkRatio >= lightRatio ? darkCandidate : lightCandidate;
            if (ContrastRatio(CompositeOver(background, chosen), background) >= targetRatio)
            {
                return chosen;
            }

            // Final fallback: stronger alpha + stronger push toward the higher-contrast tone.
            var fallbackTarget = darkRatio >= lightRatio ? darkTarget : lightTarget;
            return BlendOverlay(chosen, fallbackTarget, 0.45, Math.Max(chosen.A, (byte)0xB8));
        }

        private static Color Hex(string rgb, byte alpha = 0xFF)
        {
            if (string.IsNullOrWhiteSpace(rgb))
            {
                return Color.FromArgb(alpha, 0, 0, 0);
            }

            var value = rgb.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.Length != 6)
            {
                return Color.FromArgb(alpha, 0, 0, 0);
            }

            byte r = Convert.ToByte(value.Substring(0, 2), 16);
            byte g = Convert.ToByte(value.Substring(2, 2), 16);
            byte b = Convert.ToByte(value.Substring(4, 2), 16);
            return Color.FromArgb(alpha, r, g, b);
        }

        private static Color BlendRgb(Color baseColor, Color tint, double ratio)
        {
            ratio = Math.Max(0, Math.Min(1, ratio));
            return Color.FromArgb(
                baseColor.A,
                LerpByte(baseColor.R, tint.R, ratio),
                LerpByte(baseColor.G, tint.G, ratio),
                LerpByte(baseColor.B, tint.B, ratio));
        }

        private static Color JitterColor(Color source, int amount)
        {
            return Color.FromArgb(
                source.A,
                ClampToByte(source.R + Random.Shared.Next(-amount, amount + 1)),
                ClampToByte(source.G + Random.Shared.Next(-amount, amount + 1)),
                ClampToByte(source.B + Random.Shared.Next(-amount, amount + 1)));
        }

        private static Color Desaturate(Color source, double amount)
        {
            var clamped = Math.Max(0, Math.Min(1, amount));
            var gray = (int)Math.Round((0.299 * source.R) + (0.587 * source.G) + (0.114 * source.B));
            return Color.FromArgb(
                source.A,
                LerpByte(source.R, (byte)gray, clamped),
                LerpByte(source.G, (byte)gray, clamped),
                LerpByte(source.B, (byte)gray, clamped));
        }

        private static Color BlendOverlay(Color source, Color target, double ratio, byte alpha)
        {
            var clamped = Math.Max(0, Math.Min(1, ratio));
            return Color.FromArgb(
                alpha,
                LerpByte(source.R, target.R, clamped),
                LerpByte(source.G, target.G, clamped),
                LerpByte(source.B, target.B, clamped));
        }

        private static Color CompositeOver(Color background, Color overlay)
        {
            var a = overlay.A / 255.0;
            return Color.FromArgb(
                0xFF,
                LerpByte(background.R, overlay.R, a),
                LerpByte(background.G, overlay.G, a),
                LerpByte(background.B, overlay.B, a));
        }

        private static double ContrastRatio(Color a, Color b)
        {
            var la = RelativeLuminance(a);
            var lb = RelativeLuminance(b);
            var hi = Math.Max(la, lb);
            var lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            var r = Linearize(color.R / 255.0);
            var g = Linearize(color.G / 255.0);
            var b = Linearize(color.B / 255.0);
            return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        }

        private static double Linearize(double c)
        {
            if (c <= 0.03928)
            {
                return c / 12.92;
            }

            return Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static byte LerpByte(byte from, byte to, double ratio)
        {
            return ClampToByte((int)Math.Round(from + ((to - from) * ratio)));
        }

        private static byte ClampToByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 255)
            {
                return 255;
            }

            return (byte)value;
        }

        private sealed class ThemeTokens
        {
            public Color BackgroundInner { get; set; }
            public Color BackgroundMiddle { get; set; }
            public Color BackgroundOuter { get; set; }
            public Color Border { get; set; }
            public Color Label { get; set; }
            public Color Icon { get; set; }
            public Color Separator { get; set; }
            public Color ButtonHover { get; set; }
            public Color ButtonPressed { get; set; }
            public Color ButtonDisabled { get; set; }
            public Color NotificationAccent { get; set; }
            public Color HighlightA { get; set; }
            public Color HighlightB { get; set; }
            public string FontFamily { get; set; } = "Segoe UI";
            public int HighlightJitter { get; set; } = 16;
            public double RandomInnerBlend { get; set; } = 0.25;
            public double RandomMiddleBlend { get; set; } = 0.20;
            public double RandomOuterBlend { get; set; } = 0.18;
        }
    }
}
