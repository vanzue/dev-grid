// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using TopToolbar.Logging;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private void ResizeToContent()
        {
            if (ToolbarContainer == null || MainStack == null)
            {
                return;
            }

            try
            {
                MainStack.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            catch (COMException ex)
            {
                AppLogger.LogWarning($"ToolbarWindow.ResizeToContent: skipped due COM exception - {ex.Message}");
                return;
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.LogWarning($"ToolbarWindow.ResizeToContent: skipped due invalid operation - {ex.Message}");
                return;
            }

            try
            {
                double scale = ToolbarContainer.XamlRoot?.RasterizationScale ?? 1.0;

                double desiredWidthDip = MainStack.DesiredSize.Width + ToolbarContainer.Padding.Left + ToolbarContainer.Padding.Right;
                double desiredHeightDip = MainStack.DesiredSize.Height + ToolbarContainer.Padding.Top + ToolbarContainer.Padding.Bottom;
                if (double.IsNaN(desiredHeightDip) || desiredHeightDip <= 0)
                {
                    desiredHeightDip = ToolbarContainer.ActualHeight > 0 ? ToolbarContainer.ActualHeight : ToolbarMetrics.ToolbarHeight;
                }
                else
                {
                    desiredHeightDip = Math.Max(desiredHeightDip, ToolbarMetrics.ToolbarHeight);
                }

                var displayArea = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary);
                double maxWidthDip = desiredWidthDip;
                double maxHeightDip = desiredHeightDip;
                if (displayArea != null)
                {
                    double shadowPadding = ToolbarMetrics.ToolbarShadowPaddingValue;
                    maxWidthDip = Math.Max(ToolbarMetrics.ToolbarButtonSize, (displayArea.WorkArea.Width / scale) - (shadowPadding * 2));
                    maxHeightDip = Math.Max(ToolbarMetrics.ToolbarButtonSize, (displayArea.WorkArea.Height / scale) - (shadowPadding * 2));
                }

                double widthDip = Math.Min(desiredWidthDip, maxWidthDip);
                double heightDip = Math.Min(desiredHeightDip, maxHeightDip);
                double widthWithShadowDip = widthDip + (ToolbarMetrics.ToolbarShadowPaddingValue * 2);
                double heightWithShadowDip = heightDip + (ToolbarMetrics.ToolbarShadowPaddingValue * 2);

                int widthPx = (int)Math.Ceiling(widthWithShadowDip * scale);
                int heightPx = (int)Math.Ceiling(heightWithShadowDip * scale);

                this.AppWindow.Resize(new Windows.Graphics.SizeInt32(widthPx, heightPx));
                _topBarTriggerWidth = Math.Max(widthPx, TriggerWindowMinWidth);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"ToolbarWindow.ResizeToContent: skipped due exception - {ex.Message}");
            }
        }

        private void PositionAtTopCenter()
        {
            var displayArea = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            int width = this.AppWindow.Size.Width;
            int height = this.AppWindow.Size.Height;
            int x = workArea.X + ((workArea.Width - width) / 2);
            int y = workArea.Y - height; // hidden above top
            this.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
    }
}
