// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Providers;
using TopToolbar.Services;
using TopToolbar.Services.Workspaces;

namespace TopToolbar.Services.HotCorners
{
    internal sealed class HotCornerActionRouter
    {
        private static readonly IntPtr HwndBroadcast = new(0xFFFF);
        private const int KeyEventKeyUp = 0x0002;
        private const int VkLeftWindows = 0x5B;
        private const int VkD = 0x44;
        private const int VkTab = 0x09;
        private const int WmSysCommand = 0x0112;
        private const int ScScreenSave = 0xF140;
        private const int ScMonitorPower = 0xF170;
        private const int MonitorPowerOff = 2;

        private readonly NotificationService _notifications;
        private int _busy;

        public HotCornerActionRouter(NotificationService notifications)
        {
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        }

        public event Func<string, string, Task> SnapshotCompleted;

        public async Task ExecuteAsync(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId) ||
                string.Equals(actionId, HotCornerActions.None, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Exchange(ref _busy, 1) == 1)
            {
                AppLogger.LogInfo("HotCorner: action ignored, another action is in progress.");
                return;
            }

            try
            {
                switch (actionId)
                {
                    case HotCornerActions.Snapshot:
                        await SnapshotAsync().ConfigureAwait(false);
                        break;
                    case HotCornerActions.ShowDesktop:
                        ShowDesktop();
                        break;
                    case HotCornerActions.TaskView:
                        ShowTaskView();
                        break;
                    case HotCornerActions.LockScreen:
                        LockScreen();
                        break;
                    case HotCornerActions.StartScreenSaver:
                        StartScreenSaver();
                        break;
                    case HotCornerActions.TurnOffDisplay:
                        TurnOffDisplay();
                        break;
                    default:
                        AppLogger.LogWarning($"HotCorner: unknown action id '{actionId}'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("HotCorner: action execution failed.", ex);
                _notifications.ShowError("Hot corner action failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private async Task SnapshotAsync()
        {
            var name = await WorkspaceNameSuggester
                .GetNextWorkspaceNameAsync(CancellationToken.None)
                .ConfigureAwait(false);

            AppLogger.LogInfo($"HotCorner: silent snapshot starting, name='{name}'.");

            using var provider = new WorkspaceProvider();
            var workspace = await provider.SnapshotAsync(name, CancellationToken.None).ConfigureAwait(false);

            if (workspace == null)
            {
                AppLogger.LogWarning("HotCorner: snapshot returned null workspace.");
                _notifications.ShowWarning("Snapshot failed: no eligible windows detected.");
                return;
            }

            AppLogger.LogInfo($"HotCorner: snapshot saved id='{workspace.Id}', name='{workspace.Name}'.");
            _notifications.ShowSuccess($"Workspace '{workspace.Name}' captured.");

            var handler = SnapshotCompleted;
            if (handler != null)
            {
                try
                {
                    await handler.Invoke(workspace.Id, workspace.Name).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"HotCorner: snapshot completion handler failed - {ex.Message}");
                }
            }
        }

        private static void ShowDesktop()
        {
            AppLogger.LogInfo("HotCorner: show desktop.");
            SendWindowsShortcut(VkD);
        }

        private static void ShowTaskView()
        {
            AppLogger.LogInfo("HotCorner: task view.");
            SendWindowsShortcut(VkTab);
        }

        private static void LockScreen()
        {
            AppLogger.LogInfo("HotCorner: lock screen.");
            if (!LockWorkStation())
            {
                var error = Marshal.GetLastWin32Error();
                AppLogger.LogWarning($"HotCorner: LockWorkStation failed, error={error}.");
            }
        }

        private static void StartScreenSaver()
        {
            AppLogger.LogInfo("HotCorner: start screen saver.");
            PostMessage(HwndBroadcast, WmSysCommand, new IntPtr(ScScreenSave), IntPtr.Zero);
        }

        private static void TurnOffDisplay()
        {
            AppLogger.LogInfo("HotCorner: turn off display.");
            PostMessage(HwndBroadcast, WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(MonitorPowerOff));
        }

        private static void SendWindowsShortcut(int virtualKey)
        {
            keybd_event((byte)VkLeftWindows, 0, 0, UIntPtr.Zero);
            keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event((byte)virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
            keybd_event((byte)VkLeftWindows, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);
    }
}
