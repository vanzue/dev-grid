// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspaceThumbnailRenderer
    {
        private const int CanvasSize = 256;
        private const float CanvasPadding = 18f;
        private const float MonitorCornerRadius = 18f;
        private const float WindowCornerRadius = 8f;
        private const float MinimumWindowSize = 8f;
        private static readonly Color[] WindowPalette =
        {
            Color.FromArgb(255, 0, 190, 166),
            Color.FromArgb(255, 255, 138, 76),
            Color.FromArgb(255, 0, 151, 230),
            Color.FromArgb(255, 88, 204, 104),
            Color.FromArgb(255, 255, 196, 61),
            Color.FromArgb(255, 255, 94, 98),
            Color.FromArgb(255, 44, 123, 229),
            Color.FromArgb(255, 247, 112, 181),
            Color.FromArgb(255, 127, 221, 86),
            Color.FromArgb(255, 255, 171, 0),
        };

        public string Render(WorkspaceDefinition workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);

            if (string.IsNullOrWhiteSpace(workspace.Id))
            {
                throw new ArgumentException("Workspace id is required to render a thumbnail.", nameof(workspace));
            }

            var monitors = GetValidMonitors(workspace);
            var applications = workspace.Applications?.Where(app => app?.Position != null && !app.Position.IsEmpty).ToList()
                ?? new List<ApplicationDefinition>();
            if (monitors.Count == 0 && applications.Count == 0)
            {
                throw new InvalidOperationException("Workspace thumbnail requires at least one monitor or application.");
            }

            var layoutBounds = monitors.Count > 0
                ? UnionMonitorBounds(monitors)
                : UnionApplicationBounds(applications);
            if (layoutBounds.Width <= 0 || layoutBounds.Height <= 0)
            {
                throw new InvalidOperationException("Workspace thumbnail bounds are empty.");
            }

            var path = WorkspaceStoragePaths.GetWorkspaceIconPath(workspace.Id, DateTimeOffset.UtcNow.Ticks);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var bitmap = new Bitmap(CanvasSize, CanvasSize, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            ConfigureGraphics(graphics);
            graphics.Clear(Color.Transparent);

            var mapper = new LayoutMapper(layoutBounds);
            DrawBackground(graphics);
            DrawMonitors(graphics, monitors, mapper);
            DrawWindows(graphics, workspace, applications, monitors, mapper);
            DrawMinimizedChips(graphics, applications);

            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                bitmap.Save(tempPath, ImageFormat.Png);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            return path;
        }

        private static void ConfigureGraphics(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
        }

        private static void DrawBackground(Graphics graphics)
        {
            using var path = CreateRoundedRectangle(new RectangleF(0, 0, CanvasSize, CanvasSize), 34f);
            using var brush = new LinearGradientBrush(
                new RectangleF(0, 0, CanvasSize, CanvasSize),
                Color.FromArgb(255, 4, 31, 45),
                Color.FromArgb(255, 1, 13, 24),
                LinearGradientMode.ForwardDiagonal);
            graphics.FillPath(brush, path);

            using var glowBrush = new SolidBrush(Color.FromArgb(42, 0, 214, 186));
            graphics.FillEllipse(glowBrush, -42, -28, 160, 124);
            using var warmGlowBrush = new SolidBrush(Color.FromArgb(34, 255, 151, 74));
            graphics.FillEllipse(warmGlowBrush, 122, 156, 174, 128);
        }

        private static void DrawMonitors(Graphics graphics, IReadOnlyList<MonitorDefinition> monitors, LayoutMapper mapper)
        {
            if (monitors.Count == 0)
            {
                return;
            }

            foreach (var monitor in monitors)
            {
                var rect = mapper.Map(monitor.DpiAwareRect.Left, monitor.DpiAwareRect.Top, monitor.DpiAwareRect.Width, monitor.DpiAwareRect.Height);
                if (rect.Width <= 1f || rect.Height <= 1f)
                {
                    continue;
                }

                using var shadow = CreateRoundedRectangle(Offset(rect, 0f, 2f), MonitorCornerRadius);
                using var shadowBrush = new SolidBrush(Color.FromArgb(92, 0, 0, 0));
                graphics.FillPath(shadowBrush, shadow);

                using var surface = CreateRoundedRectangle(rect, MonitorCornerRadius);
                using var fill = new SolidBrush(Color.FromArgb(244, 255, 243, 213));
                using var stroke = new Pen(Color.FromArgb(255, 255, 252, 238), 1.6f);
                graphics.FillPath(fill, surface);
                graphics.DrawPath(stroke, surface);
            }
        }

        private static void DrawWindows(
            Graphics graphics,
            WorkspaceDefinition workspace,
            IReadOnlyList<ApplicationDefinition> applications,
            IReadOnlyList<MonitorDefinition> monitors,
            LayoutMapper mapper)
        {
            var visibleApps = applications
                .Where(app => !app.Minimized)
                .OrderByDescending(app => app.ZOrder)
                .ToList();

            for (var index = 0; index < visibleApps.Count; index++)
            {
                var app = visibleApps[index];
                var sourceRect = GetApplicationRect(app, monitors);
                var rect = mapper.Map(sourceRect.Left, sourceRect.Top, sourceRect.Width, sourceRect.Height);
                if (rect.Width <= 1f || rect.Height <= 1f)
                {
                    continue;
                }

                rect.Width = Math.Max(rect.Width, MinimumWindowSize);
                rect.Height = Math.Max(rect.Height, MinimumWindowSize);

                var baseColor = GetWindowColor(index);
                using var shadowPath = CreateRoundedRectangle(Offset(rect, 1f, 2f), WindowCornerRadius);
                using var shadowBrush = new SolidBrush(Color.FromArgb(118, 0, 0, 0));
                graphics.FillPath(shadowBrush, shadowPath);

                using var windowPath = CreateRoundedRectangle(rect, WindowCornerRadius);
                using var fill = new SolidBrush(Color.FromArgb(248, baseColor));
                graphics.FillPath(fill, windowPath);

                var headerHeight = Math.Min(6f, Math.Max(3f, rect.Height * 0.18f));
                var headerRect = new RectangleF(rect.Left, rect.Top, rect.Width, headerHeight);
                using var clipPath = CreateRoundedRectangle(rect, WindowCornerRadius);
                var state = graphics.Save();
                graphics.SetClip(clipPath);
                using var headerBrush = new SolidBrush(Color.FromArgb(116, 255, 255, 255));
                graphics.FillRectangle(headerBrush, headerRect);
                graphics.Restore(state);

                var isFocused = !string.IsNullOrWhiteSpace(workspace.FocusedApplicationId)
                    && string.Equals(workspace.FocusedApplicationId, app.Id, StringComparison.OrdinalIgnoreCase);
                using var stroke = new Pen(isFocused ? Color.FromArgb(255, 255, 252, 238) : Color.FromArgb(220, 255, 252, 238), isFocused ? 3.0f : 1.4f);
                graphics.DrawPath(stroke, windowPath);
            }
        }

        private static void DrawMinimizedChips(Graphics graphics, IReadOnlyList<ApplicationDefinition> applications)
        {
            var minimized = applications
                .Where(app => app.Minimized)
                .OrderBy(app => app.ZOrder)
                .Take(9)
                .ToList();
            if (minimized.Count == 0)
            {
                return;
            }

            var gap = 5f;
            var chipWidth = Math.Min(20f, (CanvasSize - (CanvasPadding * 2f) - ((minimized.Count - 1) * gap)) / minimized.Count);
            var chipHeight = 7f;
            var totalWidth = (chipWidth * minimized.Count) + (gap * (minimized.Count - 1));
            var startX = (CanvasSize - totalWidth) / 2f;
            var y = CanvasSize - 19f;

            for (var i = 0; i < minimized.Count; i++)
            {
                var rect = new RectangleF(startX + (i * (chipWidth + gap)), y, chipWidth, chipHeight);
                using var path = CreateRoundedRectangle(rect, chipHeight / 2f);
                using var fill = new SolidBrush(Color.FromArgb(190, GetWindowColor(i + applications.Count)));
                using var stroke = new Pen(Color.FromArgb(82, 255, 255, 255), 1f);
                graphics.FillPath(fill, path);
                graphics.DrawPath(stroke, path);
            }
        }

        private static RectangleF GetApplicationRect(ApplicationDefinition app, IReadOnlyList<MonitorDefinition> monitors)
        {
            if (app.Maximized && monitors != null)
            {
                var monitor = monitors.FirstOrDefault(item => item.Number == app.MonitorIndex);
                if (monitor?.DpiAwareRect != null && monitor.DpiAwareRect.Width > 0 && monitor.DpiAwareRect.Height > 0)
                {
                    return new RectangleF(
                        monitor.DpiAwareRect.Left,
                        monitor.DpiAwareRect.Top,
                        monitor.DpiAwareRect.Width,
                        monitor.DpiAwareRect.Height);
                }
            }

            return new RectangleF(app.Position.X, app.Position.Y, app.Position.Width, app.Position.Height);
        }

        private static Color GetWindowColor(int index)
        {
            return WindowPalette[Math.Abs(index) % WindowPalette.Length];
        }

        private static Color FromHsl(uint hue, double saturation, double lightness)
        {
            var h = hue / 360d;
            double r;
            double g;
            double b;

            if (saturation == 0d)
            {
                r = g = b = lightness;
            }
            else
            {
                var q = lightness < 0.5d
                    ? lightness * (1d + saturation)
                    : lightness + saturation - (lightness * saturation);
                var p = (2d * lightness) - q;
                r = HueToRgb(p, q, h + (1d / 3d));
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - (1d / 3d));
            }

            return Color.FromArgb(
                (int)Math.Round(r * 255d),
                (int)Math.Round(g * 255d),
                (int)Math.Round(b * 255d));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0d)
            {
                t += 1d;
            }

            if (t > 1d)
            {
                t -= 1d;
            }

            if (t < 1d / 6d)
            {
                return p + ((q - p) * 6d * t);
            }

            if (t < 1d / 2d)
            {
                return q;
            }

            if (t < 2d / 3d)
            {
                return p + ((q - p) * ((2d / 3d) - t) * 6d);
            }

            return p;
        }

        private static IReadOnlyList<MonitorDefinition> GetValidMonitors(WorkspaceDefinition workspace)
        {
            return workspace.Monitors?
                .Where(monitor => monitor?.DpiAwareRect != null && monitor.DpiAwareRect.Width > 0 && monitor.DpiAwareRect.Height > 0)
                .ToList()
                ?? new List<MonitorDefinition>();
        }

        private static RectangleF UnionMonitorBounds(IReadOnlyList<MonitorDefinition> monitors)
        {
            var left = monitors.Min(monitor => monitor.DpiAwareRect.Left);
            var top = monitors.Min(monitor => monitor.DpiAwareRect.Top);
            var right = monitors.Max(monitor => monitor.DpiAwareRect.Left + monitor.DpiAwareRect.Width);
            var bottom = monitors.Max(monitor => monitor.DpiAwareRect.Top + monitor.DpiAwareRect.Height);
            return new RectangleF(left, top, right - left, bottom - top);
        }

        private static RectangleF UnionApplicationBounds(IReadOnlyList<ApplicationDefinition> applications)
        {
            var left = applications.Min(app => app.Position.X);
            var top = applications.Min(app => app.Position.Y);
            var right = applications.Max(app => app.Position.X + app.Position.Width);
            var bottom = applications.Max(app => app.Position.Y + app.Position.Height);
            return new RectangleF(left, top, right - left, bottom - top);
        }

        private static RectangleF Offset(RectangleF rect, float x, float y)
        {
            return new RectangleF(rect.Left + x, rect.Top + y, rect.Width, rect.Height);
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            if (diameter <= 0f)
            {
                path.AddRectangle(rect);
                return path;
            }

            var arc = new RectangleF(rect.Left, rect.Top, diameter, diameter);
            path.AddArc(arc, 180f, 90f);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270f, 90f);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0f, 90f);
            arc.X = rect.Left;
            path.AddArc(arc, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private sealed class LayoutMapper
        {
            private readonly RectangleF _sourceBounds;
            private readonly float _scale;
            private readonly float _offsetX;
            private readonly float _offsetY;

            public LayoutMapper(RectangleF sourceBounds)
            {
                _sourceBounds = sourceBounds;
                var available = CanvasSize - (CanvasPadding * 2f);
                _scale = Math.Min(available / sourceBounds.Width, available / sourceBounds.Height);
                var scaledWidth = sourceBounds.Width * _scale;
                var scaledHeight = sourceBounds.Height * _scale;
                _offsetX = (CanvasSize - scaledWidth) / 2f;
                _offsetY = (CanvasSize - scaledHeight) / 2f;
            }

            public RectangleF Map(float left, float top, float width, float height)
            {
                return new RectangleF(
                    _offsetX + ((left - _sourceBounds.Left) * _scale),
                    _offsetY + ((top - _sourceBounds.Top) * _scale),
                    width * _scale,
                    height * _scale);
            }
        }
    }
}
