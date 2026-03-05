// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Display;
using TopToolbar.Services.Windowing;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspaceSnapshotter
    {
        private const string ApplicationFrameHostProcessName = "ApplicationFrameHost.exe";
        private static readonly HashSet<string> ExcludedWindowClasses = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "TaskListThumbnailWnd",
            "Progman",
            "WorkerW",
            "NotifyIconOverflowWindow",
            "SysShadow",
            "SearchPane",
            "SearchHost",
            "Windows.UI.Core.CoreWindow",
            "NativeHWNDHost",
            "ApplicationManager_DesktopShellWindow",
            "LauncherTipWndClass",
        };

        private static readonly HashSet<string> ExcludedWindowTitles = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "Program Manager",
        };

        private readonly WorkspaceDefinitionStore _definitionStore;
        private readonly DisplayManager _displayManager;
        private readonly WindowManager _windowManager;
        private readonly ManagedWindowRegistry _managedWindows;
        public WorkspaceSnapshotter(
            WorkspaceDefinitionStore definitionStore,
            WindowManager windowManager,
            DisplayManager displayManager,
            ManagedWindowRegistry managedWindows)
        {
            _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _displayManager = displayManager ?? throw new ArgumentNullException(nameof(displayManager));
            _managedWindows = managedWindows ?? throw new ArgumentNullException(nameof(managedWindows));
        }

        public async Task<WorkspaceDefinition> SnapshotAsync(
            string workspaceName,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(workspaceName))
            {
                throw new ArgumentException(
                    "Workspace name cannot be null or empty.",
                    nameof(workspaceName)
                );
            }

            var trimmedName = workspaceName.Trim();
            var monitors = _displayManager.GetSnapshot();
            var windows = _windowManager.GetSnapshot();
            var applications = new List<ApplicationDefinition>();
            var windowBindings = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            var focusedHandle = IntPtr.Zero;
            WindowInfo focusedWindowInfo = null;
            if (NativeWindowHelper.TryGetForegroundWindowHandle(out var currentForeground))
            {
                focusedHandle = currentForeground;
                _ = NativeWindowHelper.TryCreateWindowInfo(focusedHandle, out focusedWindowInfo);
            }

            string focusedApplicationId = string.Empty;

            foreach (var window in windows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShouldCaptureWindow(window, windows, out var effectiveProcessPath))
                {
                    continue;
                }

                var app = CreateApplicationDefinitionFromWindow(window, monitors);
                if (app != null)
                {
                    if (!string.IsNullOrWhiteSpace(effectiveProcessPath))
                    {
                        app.Path = effectiveProcessPath;

                        var fileName = Path.GetFileName(effectiveProcessPath);
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            app.Name = fileName;
                        }
                    }

                    applications.Add(app);

                    if (window.Handle != IntPtr.Zero && !string.IsNullOrWhiteSpace(app.Id))
                    {
                        windowBindings[app.Id] = window.Handle;
                    }

                    if (string.IsNullOrWhiteSpace(focusedApplicationId)
                        && IsSnapshotFocusedWindow(window, focusedHandle, focusedWindowInfo))
                    {
                        focusedApplicationId = app.Id;
                    }
                }
            }

            if (applications.Count == 0)
            {
                return null;
            }

            var workspace = new WorkspaceDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = trimmedName,
                CreationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsShortcutNeeded = false,
                MoveExistingWindows = true,
                RuntimeSessionOnly = true,
                RuntimeSessionId = WorkspaceRuntimeSession.SessionId,
                FocusedApplicationId = focusedApplicationId,
                Applications = applications,
            };

            if (monitors.Count > 0)
            {
                var monitorDefinitions = new List<MonitorDefinition>(monitors.Count);
                foreach (var monitor in monitors)
                {
                    monitorDefinitions.Add(CreateMonitorDefinition(monitor));
                }

                workspace.Monitors = monitorDefinitions;
            }
            else
            {
                workspace.Monitors = new List<MonitorDefinition>();
            }

            await _definitionStore
                .SaveWorkspaceAsync(workspace, cancellationToken)
                .ConfigureAwait(false);

            BindSnapshotWindows(workspace, windowBindings);

            if (!string.IsNullOrWhiteSpace(focusedApplicationId))
            {
                AppLogger.LogInfo($"WorkspaceSnapshot: focused app captured id='{focusedApplicationId}' for workspace '{workspace.Id}'");
            }

            return workspace;
        }

        private static bool IsSnapshotFocusedWindow(
            WindowInfo candidate,
            IntPtr focusedHandle,
            WindowInfo focusedWindowInfo)
        {
            if (candidate == null)
            {
                return false;
            }

            if (focusedHandle != IntPtr.Zero && candidate.Handle == focusedHandle)
            {
                return true;
            }

            if (focusedWindowInfo == null || focusedWindowInfo.ProcessId == 0)
            {
                return false;
            }

            if (candidate.ProcessId != focusedWindowInfo.ProcessId)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(focusedWindowInfo.Title)
                && string.Equals(candidate.Title, focusedWindowInfo.Title, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private void BindSnapshotWindows(
            WorkspaceDefinition workspace,
            IReadOnlyDictionary<string, IntPtr> windowBindings)
        {
            if (workspace?.Applications == null || windowBindings == null || windowBindings.Count == 0)
            {
                return;
            }

            var boundCount = 0;
            foreach (var app in workspace.Applications)
            {
                if (string.IsNullOrWhiteSpace(app?.Id))
                {
                    continue;
                }

                if (windowBindings.TryGetValue(app.Id, out var hwnd) && hwnd != IntPtr.Zero)
                {
                    _managedWindows.BindWindowShared(workspace.Id, app.Id, hwnd);
                    boundCount++;
                }
            }

            if (boundCount > 0)
            {
                AppLogger.LogInfo($"WorkspaceSnapshot: bound {boundCount} window(s) for workspace '{workspace.Id}'");
            }
        }

        private bool ShouldCaptureWindow(
            WindowInfo window,
            IReadOnlyList<WindowInfo> snapshot,
            out string resolvedProcessPath
        )
        {
            resolvedProcessPath = string.Empty;

            if (window == null)
            {
                return false;
            }

            if (!window.IsVisible || window.ProcessId == (uint)Environment.ProcessId)
            {
                return false;
            }

            if (NativeWindowHelper.IsWindowCloaked(window.Handle))
            {
                return false;
            }

            if (NativeWindowHelper.TryIsWindowOnCurrentVirtualDesktop(window.Handle, out var isOnCurrentDesktop)
                && !isOnCurrentDesktop)
            {
                return false;
            }

            if (window.Bounds.IsEmpty)
            {
                return false;
            }

            resolvedProcessPath = ResolveProcessPath(window, snapshot);
            var hasIdentity = !string.IsNullOrWhiteSpace(resolvedProcessPath)
                || !string.IsNullOrWhiteSpace(window.AppUserModelId)
                || !string.IsNullOrWhiteSpace(window.PackageFullName);
            if (string.IsNullOrWhiteSpace(window.Title) && !hasIdentity)
            {
                return false;
            }

            if (IsExcludedWindow(window))
            {
                return false;
            }

            return true;
        }

        private static string ResolveProcessPath(
            WindowInfo window,
            IReadOnlyList<WindowInfo> snapshot
        )
        {
            if (window == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(window.ProcessPath) && !IsApplicationFrameHost(window))
            {
                return window.ProcessPath;
            }

            if (!IsApplicationFrameHost(window) || snapshot == null || snapshot.Count == 0)
            {
                return window.ProcessPath ?? string.Empty;
            }

            var title = window.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                return window.ProcessPath ?? string.Empty;
            }

            foreach (var candidate in snapshot)
            {
                if (candidate == null || candidate.Handle == window.Handle)
                {
                    continue;
                }

                if (!candidate.IsVisible || candidate.Bounds.IsEmpty)
                {
                    continue;
                }

                if (NativeWindowHelper.IsWindowCloaked(candidate.Handle))
                {
                    continue;
                }

                if (NativeWindowHelper.TryIsWindowOnCurrentVirtualDesktop(candidate.Handle, out var isOnCurrentDesktop)
                    && !isOnCurrentDesktop)
                {
                    continue;
                }

                if (!string.Equals(candidate.Title, title, StringComparison.Ordinal))
                {
                    continue;
                }

                if (
                    candidate.ProcessId != window.ProcessId
                    && !string.IsNullOrWhiteSpace(candidate.ProcessPath)
                )
                {
                    return candidate.ProcessPath;
                }
            }

            return window.ProcessPath ?? string.Empty;
        }

        private static bool IsApplicationFrameHost(WindowInfo window)
        {
            if (window == null)
            {
                return false;
            }

            return string.Equals(
                window.ProcessFileName,
                ApplicationFrameHostProcessName,
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static bool IsExcludedWindow(WindowInfo window)
        {
            if (window == null)
            {
                return true;
            }

            if (
                !string.IsNullOrWhiteSpace(window.ClassName)
                && ExcludedWindowClasses.Contains(window.ClassName)
            )
            {
                return true;
            }

            if (
                !string.IsNullOrWhiteSpace(window.Title)
                && ExcludedWindowTitles.Contains(window.Title)
            )
            {
                return true;
            }

            if (NativeWindowHelper.HasToolWindowStyle(window.Handle))
            {
                return true;
            }

            return false;
        }

        private static MonitorDefinition CreateMonitorDefinition(DisplayMonitor monitor)
        {
            if (monitor == null)
            {
                return new MonitorDefinition();
            }

            return new MonitorDefinition
            {
                Id = monitor.Id ?? string.Empty,
                InstanceId = monitor.InstanceId ?? string.Empty,
                Number = monitor.Index,
                Dpi = monitor.Dpi,
                DpiAwareRect = new MonitorDefinition.MonitorRect
                {
                    Left = monitor.DpiAwareRect.Left,
                    Top = monitor.DpiAwareRect.Top,
                    Width = monitor.DpiAwareRect.Width,
                    Height = monitor.DpiAwareRect.Height,
                },
                DpiUnawareRect = new MonitorDefinition.MonitorRect
                {
                    Left = monitor.DpiUnawareRect.Left,
                    Top = monitor.DpiUnawareRect.Top,
                    Width = monitor.DpiUnawareRect.Width,
                    Height = monitor.DpiUnawareRect.Height,
                },
                DpiAwareWorkRect = new MonitorDefinition.MonitorRect
                {
                    Left = monitor.DpiAwareWorkRect.Left,
                    Top = monitor.DpiAwareWorkRect.Top,
                    Width = monitor.DpiAwareWorkRect.Width,
                    Height = monitor.DpiAwareWorkRect.Height,
                },
                DpiUnawareWorkRect = new MonitorDefinition.MonitorRect
                {
                    Left = monitor.DpiUnawareWorkRect.Left,
                    Top = monitor.DpiUnawareWorkRect.Top,
                    Width = monitor.DpiUnawareWorkRect.Width,
                    Height = monitor.DpiUnawareWorkRect.Height,
                },
            };
        }

        private ApplicationDefinition CreateApplicationDefinitionFromWindow(
            WindowInfo window,
            IReadOnlyList<DisplayMonitor> monitors
        )
        {
            if (window == null)
            {
                return null;
            }

            var bounds = window.Bounds;
            if (bounds.IsEmpty)
            {
                return null;
            }

            var normalBounds = bounds;
            var isMinimized = false;
            var isMaximized = false;

            try
            {
                if (
                    NativeWindowHelper.TryGetWindowPlacement(
                        window.Handle,
                        out var placement,
                        out var minimized,
                        out var maximized
                    )
                )
                {
                    if (!placement.IsEmpty)
                    {
                        normalBounds = placement;
                    }

                    isMinimized = minimized;
                    isMaximized = maximized;
                }
            }
            catch { }

            if (normalBounds.IsEmpty)
            {
                return null;
            }

            var position = new ApplicationDefinition.ApplicationPosition
            {
                X = normalBounds.Left,
                Y = normalBounds.Top,
                Width = normalBounds.Width,
                Height = normalBounds.Height,
            };

            if (position.Width <= 0 || position.Height <= 0)
            {
                return null;
            }

            var definition = new ApplicationDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = !string.IsNullOrWhiteSpace(window.ProcessFileName)
                    ? window.ProcessFileName
                    : window.ProcessName,
                Title = window.Title,
                Path = window.ProcessPath ?? string.Empty,
                AppUserModelId = window.AppUserModelId ?? string.Empty,
                MonitorIndex = FindMonitorIndex(normalBounds, monitors),
                Minimized = isMinimized,
                Maximized = isMaximized,
                Position = position,
                CommandLineArguments = string.Empty,
                PackageFullName = window.PackageFullName ?? string.Empty,
                PwaAppId = string.Empty,
                Version = string.Empty,
                IsElevated = false,
                CanLaunchElevated = false,
            };

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                definition.Name = string.Empty;
            }

            return definition;
        }

        private static int FindMonitorIndex(
            WindowBounds bounds,
            IReadOnlyList<DisplayMonitor> monitors
        )
        {
            if (monitors == null || monitors.Count == 0)
            {
                return 0;
            }

            var centerX = bounds.Left + (bounds.Width / 2);
            var centerY = bounds.Top + (bounds.Height / 2);

            var bestIndex = 0;
            long bestArea = -1;

            for (int i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i].Bounds;
                if (
                    centerX >= monitor.Left
                    && centerX < monitor.Right
                    && centerY >= monitor.Top
                    && centerY < monitor.Bottom
                )
                {
                    return monitors[i].Index;
                }

                var area = CalculateIntersectionArea(bounds, monitor);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestIndex = monitors[i].Index;
                }
            }

            return bestIndex;
        }

        private static long CalculateIntersectionArea(WindowBounds window, DisplayRect monitor)
        {
            var left = Math.Max(window.Left, monitor.Left);
            var top = Math.Max(window.Top, monitor.Top);
            var right = Math.Min(window.Right, monitor.Right);
            var bottom = Math.Min(window.Bottom, monitor.Bottom);

            var width = right - left;
            var height = bottom - top;
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            return (long)width * height;
        }
    }
}
