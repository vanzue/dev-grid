// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Timers;
using Microsoft.UI.Windowing;
using Timer = System.Timers.Timer;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private const int TriggerWindowHeight = 10;
        private const int TriggerWindowMinWidth = 320;
        private const int VkControl = 0x11;

        private void StartMonitoring()
        {
            if (_monitorTimer != null)
            {
                _monitorTimer.Start();
                return;
            }

            _monitorTimer = new Timer(120);
            _monitorTimer.Elapsed += MonitorTimer_Elapsed;
            _monitorTimer.AutoReset = true;
            _monitorTimer.Start();
        }

        private void StopMonitoring()
        {
            _monitorTimer?.Stop();
        }

        private void MonitorTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (_currentDisplayMode != Models.ToolbarDisplayMode.TopBar)
            {
                return;
            }

            GetCursorPos(out var pt);
            var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary);
            GetTriggerWindowBounds(displayArea, out var triggerLeft, out var triggerTop, out var triggerRight, out var triggerBottom);

            var inTriggerWindow = IsPointInRect(pt.X, pt.Y, triggerLeft, triggerTop, triggerRight, triggerBottom);
            var ctrlGatePassed = !_requireCtrlForTopBarTrigger || IsCtrlPressed();
            var shouldShow = inTriggerWindow && ctrlGatePassed;

            if (shouldShow && !_isVisible)
            {
                DispatcherQueue.TryEnqueue(() => ShowToolbar());
            }
            else if (_isVisible)
            {
                // hide when cursor is not over the toolbar rectangle
                DispatcherQueue.TryEnqueue(() =>
                {
                    var winPos = this.AppWindow.Position;
                    var winSize = this.AppWindow.Size;
                    bool overToolbar = pt.X >= winPos.X && pt.X <= winPos.X + winSize.Width &&
                                       pt.Y >= winPos.Y && pt.Y <= winPos.Y + winSize.Height;
                    bool overTriggerWindow = IsPointInRect(pt.X, pt.Y, triggerLeft, triggerTop, triggerRight, triggerBottom);
                    if (!overToolbar && !overTriggerWindow)
                    {
                        HideToolbar();
                    }
                });
            }
        }

        private void ShowToolbar()
        {
            _isVisible = true;

            // Reposition to current monitor top edge
            GetCursorPos(out var ptPx);
            var da = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(ptPx.X, ptPx.Y), DisplayAreaFallback.Primary);
            var work = da.WorkArea;
            var size = AppWindow.Size;
            int x = work.X + ((work.Width - size.Width) / 2);
            int y = work.Y; // flush with top
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
            AppWindow.Show(false); // show without activation
            MakeTopMost();
            UpdateToastWindowAnchor();
        }

        private void HideToolbar(bool initial = false)
        {
            _isVisible = false;
            AppWindow.Hide();
            UpdateToastWindowAnchor();
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private void GetTriggerWindowBounds(
            DisplayArea displayArea,
            out int left,
            out int top,
            out int right,
            out int bottom)
        {
            var work = displayArea.WorkArea;
            var triggerWidth = Math.Max(_topBarTriggerWidth, TriggerWindowMinWidth);
            triggerWidth = Math.Min(triggerWidth, work.Width);

            left = work.X + ((work.Width - triggerWidth) / 2);
            top = work.Y;
            right = left + triggerWidth;
            bottom = top + Math.Max(TriggerWindowHeight, TriggerZoneHeight);
        }

        private static bool IsPointInRect(int x, int y, int left, int top, int right, int bottom)
        {
            return x >= left && x <= right && y >= top && y <= bottom;
        }

        private static bool IsCtrlPressed()
        {
            return (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        }
    }
}
