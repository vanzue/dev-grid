// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;
using TopToolbar.Logging;
using TopToolbar.Services.Windowing;

namespace TopToolbar.Services.Workspaces
{
    internal static class AppLauncher
    {
        internal readonly record struct AppWindowResult(bool Succeeded, bool LaunchedNewInstance, IReadOnlyList<WindowInfo> Windows)
        {
            public static AppWindowResult Failed => new(false, false, Array.Empty<WindowInfo>());
        }

        /// <summary>
        /// Launches an application and waits for its window to appear.
        /// This version does not use WorkspaceExecutionContext.
        /// </summary>
        public static async Task<AppWindowResult> LaunchAppAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            if (app == null)
            {
                return AppWindowResult.Failed;
            }

            // If command-line arguments are specified and we have a path, prefer Path launch
            // because AUMID/PackageFullName activation APIs don't support passing arguments.
            var hasCommandLineArgs = !string.IsNullOrWhiteSpace(app.CommandLineArguments);
            var hasPath = !string.IsNullOrWhiteSpace(app.Path);

            if (hasCommandLineArgs && hasPath)
            {
                AppLogger.LogInfo($"WorkspaceRuntime: app '{DescribeApp(app)}' has command-line arguments, using Path launch to respect them.");
                return await LaunchWin32AppSimpleAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken).ConfigureAwait(false);
            }

            // Priority: AUMID -> PackageFullName -> Path.
            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                var result = await LaunchByAppUserModelIdSimpleAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    return result;
                }
            }

            if (!string.IsNullOrWhiteSpace(app.PackageFullName))
            {
                var result = await LaunchByPackageFullNameSimpleAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    return result;
                }
            }

            if (hasPath)
            {
                return await LaunchWin32AppSimpleAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken).ConfigureAwait(false);
            }

            return AppWindowResult.Failed;
        }

        private static async Task<IReadOnlyList<WindowInfo>> WaitForAppWindowsAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            var predicate = new Func<WindowInfo, bool>(window => WorkspaceWindowMatcher.IsMatch(window, app));
            var windows = await windowManager
                .WaitForWindowsAsync(
                    predicate,
                    knownHandles ?? Array.Empty<IntPtr>(),
                    0,
                    windowWaitTimeout,
                    windowPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);

            if (windows.Count == 0)
            {
                windows = windowManager.FindMatches(predicate);
            }

            windows = PrioritizeCurrentDesktop(windows);

            if (knownHandles == null || knownHandles.Count == 0)
            {
                return windows;
            }

            var filtered = new List<WindowInfo>();
            var known = new HashSet<IntPtr>(knownHandles);
            foreach (var window in windows)
            {
                if (!known.Contains(window.Handle))
                {
                    filtered.Add(window);
                }
            }

            return filtered;
        }

        private static IReadOnlyList<WindowInfo> PrioritizeCurrentDesktop(IReadOnlyList<WindowInfo> windows)
        {
            if (windows == null || windows.Count == 0)
            {
                return Array.Empty<WindowInfo>();
            }

            var currentDesktop = new List<WindowInfo>(windows.Count);
            var otherDesktop = new List<WindowInfo>(windows.Count);

            foreach (var window in windows)
            {
                if (IsWindowOnCurrentDesktop(window))
                {
                    currentDesktop.Add(window);
                }
                else
                {
                    otherDesktop.Add(window);
                }
            }

            if (otherDesktop.Count == 0)
            {
                return currentDesktop;
            }

            if (currentDesktop.Count == 0)
            {
                return otherDesktop;
            }

            var ordered = new List<WindowInfo>(currentDesktop.Count + otherDesktop.Count);
            ordered.AddRange(currentDesktop);
            ordered.AddRange(otherDesktop);
            return ordered;
        }

        private static bool IsWindowOnCurrentDesktop(WindowInfo window)
        {
            if (window == null || window.Handle == IntPtr.Zero)
            {
                return false;
            }

            if (NativeWindowHelper.IsWindowCloaked(window.Handle))
            {
                return false;
            }

            if (!NativeWindowHelper.TryIsWindowOnCurrentVirtualDesktop(window.Handle, out var isOnCurrentDesktop))
            {
                return false;
            }

            if (!isOnCurrentDesktop)
            {
                return false;
            }

            return true;
        }

        private static async Task<AppWindowResult> LaunchByAppUserModelIdSimpleAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            try
            {
                var activationManager = (IApplicationActivationManager)new ApplicationActivationManager();
                var hr = activationManager.ActivateApplication(
                    app.AppUserModelId,
                    string.IsNullOrWhiteSpace(app.CommandLineArguments) ? string.Empty : app.CommandLineArguments,
                    ActivateOptions.None,
                    out var processId);

                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                var windows = await WaitForAppWindowsAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken)
                    .ConfigureAwait(false);

                return windows.Count > 0 
                    ? new AppWindowResult(true, true, windows) 
                    : AppWindowResult.Failed;
            }
            catch (COMException ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: ActivateApplication failed for '{DescribeApp(app)}' - 0x{ex.HResult:X8} {ex.Message}.");
                return await LaunchPackagedAppViaShellSimpleAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: Unexpected error launching '{DescribeApp(app)}' - {ex.Message}.");
                return await LaunchPackagedAppViaShellSimpleAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<AppWindowResult> LaunchPackagedAppViaShellSimpleAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:appsFolder\\{app.AppUserModelId}",
                    UseShellExecute = true,
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return AppWindowResult.Failed;
                }

                var windows = await WaitForAppWindowsAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken)
                    .ConfigureAwait(false);

                return windows.Count > 0 
                    ? new AppWindowResult(true, true, windows) 
                    : AppWindowResult.Failed;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: shell launch failed for '{app.AppUserModelId}' - {ex.Message}");
                return AppWindowResult.Failed;
            }
        }

        private static async Task<AppWindowResult> LaunchByPackageFullNameSimpleAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            try
            {
                var pm = new PackageManager();
                var package = pm.FindPackageForUser(string.Empty, app.PackageFullName);
                if (package == null)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: package '{app.PackageFullName}' not found for '{DescribeApp(app)}'.");
                    return AppWindowResult.Failed;
                }

                var entries = await package.GetAppListEntriesAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var entry = entries.FirstOrDefault();
                if (entry == null)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: no AppListEntry for package '{app.PackageFullName}' ({DescribeApp(app)}).");
                    return AppWindowResult.Failed;
                }

                var launched = await entry.LaunchAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (!launched)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: LaunchAsync returned false for package '{app.PackageFullName}' ({DescribeApp(app)}).");
                    return AppWindowResult.Failed;
                }

                var windows = await WaitForAppWindowsAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken)
                    .ConfigureAwait(false);

                return windows.Count > 0 
                    ? new AppWindowResult(true, true, windows) 
                    : AppWindowResult.Failed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: package launch failed for '{DescribeApp(app)}' ({app.PackageFullName}) - {ex.Message}");
                return AppWindowResult.Failed;
            }
        }

        private static async Task<AppWindowResult> LaunchWin32AppSimpleAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            var expandedPath = ExpandPath(app.Path);
            var resolvedWorkingDirectory = ResolveWorkingDirectory(app.WorkingDirectory);
            var useShellExecute =
                expandedPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(expandedPath);
            var effectiveArguments = BuildLaunchArguments(expandedPath, app, resolvedWorkingDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = expandedPath,
                Arguments = effectiveArguments,
                UseShellExecute = useShellExecute,
                WorkingDirectory = DetermineWorkingDirectory(expandedPath, useShellExecute, resolvedWorkingDirectory),
            };

            if (app.IsElevated && app.CanLaunchElevated)
            {
                startInfo.Verb = "runas";
                startInfo.UseShellExecute = true;
            }

            try
            {
                if (IsWindowsTerminalApp(expandedPath, app?.Name) && !string.IsNullOrWhiteSpace(resolvedWorkingDirectory))
                {
                    AppLogger.LogInfo(
                        $"WorkspaceRuntime: terminal launch args adjusted for cwd. path='{expandedPath}', cwd='{resolvedWorkingDirectory}', args='{effectiveArguments}'.");
                }

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: process start returned null for '{DescribeApp(app)}' ({expandedPath}).");
                    return AppWindowResult.Failed;
                }

                var windows = await WaitForAppWindowsAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    knownHandles,
                    cancellationToken)
                    .ConfigureAwait(false);

                return windows.Count > 0 
                    ? new AppWindowResult(true, true, windows) 
                    : AppWindowResult.Failed;
            }
            catch (Win32Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: Win32Exception launching '{DescribeApp(app)}' ({expandedPath}) - {ex.Message} ({ex.NativeErrorCode}).");
                return AppWindowResult.Failed;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: failed to start '{DescribeApp(app)}' ({expandedPath}) - {ex.Message}");
                return AppWindowResult.Failed;
            }
        }

        private static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Environment.ExpandEnvironmentVariables(path).Trim('"');
            }
            catch
            {
                return path.Trim('"');
            }
        }

        private static string DetermineWorkingDirectory(
            string path,
            bool useShellExecute,
            string configuredWorkingDirectory)
        {
            var overrideDirectory = ResolveWorkingDirectory(configuredWorkingDirectory);
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
            {
                return overrideDirectory;
            }

            if (useShellExecute)
            {
                return AppContext.BaseDirectory;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
            catch
            {
            }

            return AppContext.BaseDirectory;
        }

        private static string BuildLaunchArguments(
            string expandedPath,
            ApplicationDefinition app,
            string resolvedWorkingDirectory)
        {
            var baseArguments = string.IsNullOrWhiteSpace(app?.CommandLineArguments)
                ? string.Empty
                : app.CommandLineArguments.Trim();

            if (!IsWindowsTerminalApp(expandedPath, app?.Name))
            {
                return baseArguments;
            }

            if (string.IsNullOrWhiteSpace(resolvedWorkingDirectory))
            {
                return baseArguments;
            }

            if (ContainsWindowsTerminalStartDirectory(baseArguments))
            {
                return baseArguments;
            }

            var escapedPath = resolvedWorkingDirectory.Replace("\"", "\\\"", StringComparison.Ordinal);
            var directoryArgument = $"-d \"{escapedPath}\"";
            return string.IsNullOrWhiteSpace(baseArguments)
                ? directoryArgument
                : $"{directoryArgument} {baseArguments}";
        }

        private static bool ContainsWindowsTerminalStartDirectory(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            return arguments.Contains(" --startingDirectory ", StringComparison.OrdinalIgnoreCase)
                || arguments.StartsWith("--startingDirectory ", StringComparison.OrdinalIgnoreCase)
                || arguments.Contains(" -d ", StringComparison.OrdinalIgnoreCase)
                || arguments.StartsWith("-d ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowsTerminalApp(string path, string appName)
        {
            var normalizedPath = NormalizeProcessIdentity(path);
            var normalizedName = NormalizeProcessIdentity(appName);
            return string.Equals(normalizedPath, "wt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedPath, "windowsterminal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedName, "wt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedName, "windowsterminal", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeProcessIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            try
            {
                normalized = Path.GetFileName(normalized.Trim('"'));
            }
            catch
            {
            }

            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^4];
            }

            return normalized.Trim();
        }

        private static string ResolveWorkingDirectory(string configuredWorkingDirectory)
        {
            if (string.IsNullOrWhiteSpace(configuredWorkingDirectory))
            {
                return string.Empty;
            }

            try
            {
                var expanded = ExpandPath(configuredWorkingDirectory);
                if (string.IsNullOrWhiteSpace(expanded))
                {
                    return string.Empty;
                }

                return Directory.Exists(expanded) ? expanded : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DescribeApp(ApplicationDefinition app)
        {
            if (app == null)
            {
                return "<null>";
            }

            var parts = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(app.Name))
            {
                parts.Add(app.Name.Trim());
            }

            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                parts.Add(app.Path.Trim());
            }

            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                parts.Add(app.AppUserModelId.Trim());
            }

            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(app.Title))
            {
                parts.Add(app.Title.Trim());
            }

            var identity = string.Join(" | ", parts);
            var id = string.IsNullOrWhiteSpace(app.Id) ? "<no-id>" : app.Id;
            return string.IsNullOrWhiteSpace(identity) ? $"id={id}" : $"{identity} (id={id})";
        }

        [ComImport]
        [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            int ActivateApplication(string appUserModelId, string arguments, ActivateOptions options, out uint processId);

            int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);

            int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
        }

        [ComImport]
        [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
        private class ApplicationActivationManager
        {
        }

        [Flags]
        private enum ActivateOptions
        {
            None = 0x0,
            DesignMode = 0x1,
            NoErrorUI = 0x2,
            NoSplashScreen = 0x4,
        }
    }
}
