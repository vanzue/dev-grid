// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
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

            // Prefer an explicit ms-avd: reconnect URI. Stale cold snapshots captured before
            // the Windows App LaunchFiles cache existed have no launch-uri, so resolve it at
            // launch time from the AUMID to reconnect the remote (Cloud PC) session.
            var effectiveLaunchUri = app.LaunchUri;
            if (string.IsNullOrWhiteSpace(effectiveLaunchUri)
                && Windows365LaunchResolver.IsRemoteAumid(app.AppUserModelId))
            {
                var remote = Windows365LaunchResolver.Resolve(app.AppUserModelId, app.Title);
                if (remote.Found)
                {
                    AppLogger.LogInfo($"WorkspaceRuntime: resolved Windows App remote launch URI at launch time for '{DescribeApp(app)}' (snapshot had no launch-uri).");
                    effectiveLaunchUri = remote.LaunchUri;
                }
            }

            if (!string.IsNullOrWhiteSpace(effectiveLaunchUri))
            {
                return await LaunchUriAsync(
                    app,
                    effectiveLaunchUri,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    cancellationToken).ConfigureAwait(false);
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

        private static async Task<AppWindowResult> LaunchUriAsync(
            ApplicationDefinition app,
            string launchUri,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            CancellationToken cancellationToken)
        {
            try
            {
                launchUri = launchUri?.Trim();
                if (string.IsNullOrWhiteSpace(launchUri))
                {
                    return AppWindowResult.Failed;
                }

                AppLogger.LogInfo($"WorkspaceRuntime: launching URI for '{DescribeApp(app)}'.");
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = launchUri,
                    UseShellExecute = true,
                });

                var windows = await WaitForAppWindowsAsync(
                    app,
                    windowManager,
                    windowWaitTimeout,
                    windowPollInterval,
                    Array.Empty<IntPtr>(),
                    cancellationToken)
                    .ConfigureAwait(false);

                return windows.Count > 0
                    ? new AppWindowResult(true, true, windows)
                    : AppWindowResult.Failed;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: URI launch failed for '{DescribeApp(app)}' - {ex.Message}");
                return AppWindowResult.Failed;
            }
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
            // IApplicationActivationManager.ActivateApplication only accepts packaged AUMIDs
            // (PackageFamilyName!AppId). Legacy / shell-registered AppIds such as "MSEdge"
            // (the Win32 Microsoft Edge) are not packaged AUMIDs and make ActivateApplication
            // throw E_INVALIDARG ("Value does not fall within the expected range"). Launch those
            // straight through shell:appsFolder instead of failing the activation call first.
            if (IsPackagedAumid(app.AppUserModelId))
            {
                try
                {
                    // Run activation on a dedicated STA thread: IApplicationActivationManager
                    // activates and foregrounds the target, and the launch pipeline otherwise
                    // runs on MTA thread-pool threads (ConfigureAwait(false)).
                    var hr = ActivateApplicationOnStaThread(
                        app.AppUserModelId,
                        string.IsNullOrWhiteSpace(app.CommandLineArguments) ? string.Empty : app.CommandLineArguments,
                        out _);

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

            // Non-packaged AUMID (legacy shell AppId): activation API does not apply.
            return await LaunchPackagedAppViaShellSimpleAsync(
                app,
                windowManager,
                windowWaitTimeout,
                windowPollInterval,
                knownHandles,
                cancellationToken).ConfigureAwait(false);
        }

        private static bool IsPackagedAumid(string appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return false;
            }

            var bang = appUserModelId.IndexOf('!');
            return bang > 0 && bang < appUserModelId.Length - 1;
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
                // Guard against opening a stray File Explorer window: explorer.exe
                // shell:appsFolder\<aumid> only activates an app when the AUMID maps to a real
                // AppListEntry. A packaged AUMID that does not resolve (e.g. a Windows365 remote
                // sub-id "...!Windows365:<cloudPcId>") makes explorer open a plain folder window.
                if (await ShouldSkipShellAppsFolderLaunchAsync(app.AppUserModelId, cancellationToken).ConfigureAwait(false))
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: skipping shell:appsFolder launch for unresolved AUMID '{app.AppUserModelId}' ({DescribeApp(app)}) to avoid opening a stray Explorer window.");
                    return AppWindowResult.Failed;
                }

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
                var package = ResolveInstalledPackage(pm, app);
                if (package == null)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: package '{app.PackageFullName}' not found for '{DescribeApp(app)}'.");
                    return AppWindowResult.Failed;
                }

                var entries = await package.GetAppListEntriesAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var entry = entries.FirstOrDefault(e =>
                        !string.IsNullOrWhiteSpace(app.AppUserModelId)
                        && string.Equals(e.AppUserModelId, app.AppUserModelId, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault();
                if (entry == null)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: no AppListEntry for package '{package.Id.FullName}' ({DescribeApp(app)}).");
                    return AppWindowResult.Failed;
                }

                var launched = await entry.LaunchAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (!launched)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: LaunchAsync returned false for package '{package.Id.FullName}' ({DescribeApp(app)}).");
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

        /// <summary>
        /// Resolves the currently-installed package for an app. Tries the exact (version-pinned)
        /// full name first, then falls back to the version-agnostic package family name so the
        /// app still launches after it has auto-updated to a newer version.
        /// </summary>
        private static Windows.ApplicationModel.Package ResolveInstalledPackage(
            PackageManager packageManager,
            ApplicationDefinition app)
        {
            if (!string.IsNullOrWhiteSpace(app.PackageFullName))
            {
                try
                {
                    var exact = packageManager.FindPackageForUser(string.Empty, app.PackageFullName);
                    if (exact != null)
                    {
                        return exact;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"WorkspaceRuntime: FindPackageForUser failed for '{app.PackageFullName}' - {ex.Message}");
                }
            }

            var familyName = GetPackageFamilyName(app);
            if (string.IsNullOrWhiteSpace(familyName))
            {
                return null;
            }

            try
            {
                var candidates = packageManager.FindPackagesForUser(string.Empty, familyName)?.ToList();
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                AppLogger.LogInfo($"WorkspaceRuntime: resolved packaged app by family name '{familyName}' ({candidates.Count} candidate(s)); stored full name '{app.PackageFullName}' was not installed (version drift).");
                return SelectBestPackage(candidates);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: FindPackagesForUser failed for family '{familyName}' - {ex.Message}");
                return null;
            }
        }

        private static string GetPackageFamilyName(ApplicationDefinition app)
        {
            if (app == null)
            {
                return string.Empty;
            }

            var aumid = app.AppUserModelId;
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                var bang = aumid.IndexOf('!');
                if (bang > 0)
                {
                    return aumid.Substring(0, bang);
                }
            }

            return GetFamilyNameFromFullName(app.PackageFullName);
        }

        private static string GetFamilyNameFromFullName(string packageFullName)
        {
            if (string.IsNullOrWhiteSpace(packageFullName))
            {
                return string.Empty;
            }

            // Package full name format: Name_Version_Architecture_ResourceId_PublisherId.
            // The family name is: Name_PublisherId.
            var parts = packageFullName.Split('_');
            if (parts.Length < 5)
            {
                return string.Empty;
            }

            return parts[0] + "_" + parts[^1];
        }

        private static Windows.ApplicationModel.Package SelectBestPackage(
            List<Windows.ApplicationModel.Package> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var preferred = MapProcessArchitecture();
            foreach (var package in candidates)
            {
                try
                {
                    if (package.Id.Architecture == preferred)
                    {
                        return package;
                    }
                }
                catch
                {
                    // Ignore packages whose identity can't be read.
                }
            }

            foreach (var package in candidates)
            {
                try
                {
                    if (package.Id.Architecture == Windows.System.ProcessorArchitecture.Neutral)
                    {
                        return package;
                    }
                }
                catch
                {
                    // Ignore packages whose identity can't be read.
                }
            }

            return candidates[0];
        }

        private static Windows.System.ProcessorArchitecture MapProcessArchitecture()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.Arm64:
                    return Windows.System.ProcessorArchitecture.Arm64;
                case Architecture.X64:
                    return Windows.System.ProcessorArchitecture.X64;
                case Architecture.X86:
                    return Windows.System.ProcessorArchitecture.X86;
                case Architecture.Arm:
                    return Windows.System.ProcessorArchitecture.Arm;
                default:
                    return Windows.System.ProcessorArchitecture.Unknown;
            }
        }

        /// <summary>
        /// Runs <see cref="IApplicationActivationManager.ActivateApplication"/> on a dedicated STA
        /// thread. The COM class is unavailable (REGDB_E_CLASSNOTREG) from the MTA thread-pool
        /// threads the launch pipeline normally runs on.
        /// </summary>
        private static int ActivateApplicationOnStaThread(
            string appUserModelId,
            string arguments,
            out uint processId)
        {
            var hr = 0;
            uint pid = 0;
            Exception threadException = null;

            var thread = new Thread(() =>
            {
                try
                {
                    var activationManager = (IApplicationActivationManager)new ApplicationActivationManager();
                    hr = activationManager.ActivateApplication(
                        appUserModelId,
                        arguments ?? string.Empty,
                        ActivateOptions.None,
                        out pid);
                }
                catch (Exception ex)
                {
                    threadException = ex;
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (threadException != null)
            {
                throw threadException;
            }

            processId = pid;
            return hr;
        }

        private static async Task<bool> ShouldSkipShellAppsFolderLaunchAsync(
            string appUserModelId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return true;
            }

            var bang = appUserModelId.IndexOf('!');
            if (bang <= 0)
            {
                // Legacy / shell-registered AUMID (e.g. "MSEdge"); explorer resolves these.
                return false;
            }

            return !await PackagedAumidResolvesAsync(appUserModelId, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> PackagedAumidResolvesAsync(
            string appUserModelId,
            CancellationToken cancellationToken)
        {
            var bang = appUserModelId.IndexOf('!');
            if (bang <= 0)
            {
                return false;
            }

            var familyName = appUserModelId.Substring(0, bang);
            try
            {
                var packageManager = new PackageManager();
                var packages = packageManager.FindPackagesForUser(string.Empty, familyName);
                if (packages == null)
                {
                    return false;
                }

                foreach (var package in packages)
                {
                    var entries = await package.GetAppListEntriesAsync().AsTask(cancellationToken).ConfigureAwait(false);
                    foreach (var entry in entries)
                    {
                        if (string.Equals(entry.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceRuntime: failed to validate AUMID '{appUserModelId}' - {ex.Message}");
                return false;
            }

            return false;
        }

        private static async Task<AppWindowResult> LaunchWin32AppSimpleAsync(
            ApplicationDefinition app,
            WindowManager windowManager,
            TimeSpan windowWaitTimeout,
            TimeSpan windowPollInterval,
            IReadOnlyCollection<IntPtr> knownHandles,
            CancellationToken cancellationToken)
        {
            var expandedPath = ResolveLaunchPath(app.Path);
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
                else if (IsVsCodeApp(expandedPath, app?.Name))
                {
                    AppLogger.LogInfo(
                        $"WorkspaceRuntime: VS Code launch args adjusted. path='{expandedPath}', cwd='{resolvedWorkingDirectory}', args='{effectiveArguments}'.");
                }
                else if (IsExplorerApp(expandedPath, app?.Name))
                {
                    AppLogger.LogInfo(
                        $"WorkspaceRuntime: Explorer launch args adjusted. path='{expandedPath}', args='{effectiveArguments}'.");
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

        private static string ResolveLaunchPath(string path)
        {
            var expanded = ExpandPath(path);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return string.Empty;
            }

            if (expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                return expanded;
            }

            if (File.Exists(expanded))
            {
                return expanded;
            }

            if (ContainsDirectorySyntax(expanded))
            {
                return expanded;
            }

            var fromPath = ResolveExecutableFromPath(expanded);
            if (!string.IsNullOrWhiteSpace(fromPath))
            {
                return fromPath;
            }

            var fromRegistry = ResolveExecutableFromAppPathsRegistry(expanded);
            if (!string.IsNullOrWhiteSpace(fromRegistry))
            {
                return fromRegistry;
            }

            var fromKnownLocations = ResolveExecutableFromKnownLocations(expanded);
            if (!string.IsNullOrWhiteSpace(fromKnownLocations))
            {
                return fromKnownLocations;
            }

            return expanded;
        }

        internal static string ResolveExecutableAlias(string executableOrPath)
        {
            return ResolveLaunchPath(executableOrPath);
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

            if (IsVsCodeApp(expandedPath, app?.Name))
            {
                baseArguments = EnsureVsCodeArguments(baseArguments, resolvedWorkingDirectory, app);
            }
            else if (IsExplorerApp(expandedPath, app?.Name))
            {
                baseArguments = EnsureExplorerArguments(baseArguments, resolvedWorkingDirectory);
            }

            if (!IsWindowsTerminalApp(expandedPath, app?.Name))
            {
                return baseArguments;
            }

            baseArguments = NormalizeWindowsTerminalArguments(baseArguments);

            var shouldForceNewWindow = app?.LaunchNewIfUnbound == true
                && !ContainsArgumentToken(baseArguments, "--window")
                && !ContainsArgumentToken(baseArguments, "-w");

            string AddNewWindowOption(string value)
            {
                if (!shouldForceNewWindow)
                {
                    return value;
                }

                return string.IsNullOrWhiteSpace(value)
                    ? "-w new"
                    : $"-w new {value}";
            }

            if (string.IsNullOrWhiteSpace(resolvedWorkingDirectory))
            {
                return AddNewWindowOption(baseArguments);
            }

            if (ContainsWindowsTerminalStartDirectory(baseArguments))
            {
                return AddNewWindowOption(baseArguments);
            }

            var escapedPath = resolvedWorkingDirectory.Replace("\"", "\\\"", StringComparison.Ordinal);
            var directoryArgument = $"-d \"{escapedPath}\"";
            var withDirectory = string.IsNullOrWhiteSpace(baseArguments)
                ? directoryArgument
                : $"{directoryArgument} {baseArguments}";
            return AddNewWindowOption(withDirectory);
        }

        private static string EnsureTerminalCommandUsesWorkingDirectory(string arguments, string workingDirectory)
        {
            var value = (arguments ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(workingDirectory))
            {
                return value;
            }

            var hasPwsh = value.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasPwsh)
            {
                return value;
            }

            var commandMarker = " -Command ";
            var commandIndex = value.IndexOf(commandMarker, StringComparison.OrdinalIgnoreCase);
            if (commandIndex < 0)
            {
                commandMarker = "-Command ";
                commandIndex = value.IndexOf(commandMarker, StringComparison.OrdinalIgnoreCase);
                if (commandIndex < 0)
                {
                    return value;
                }
            }

            if (value.IndexOf("Set-Location", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return value;
            }

            var markerStart = commandIndex + commandMarker.Length;
            var prefix = value.Substring(0, markerStart).TrimEnd();
            var rawCommand = value.Substring(markerStart).Trim();
            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                return value;
            }

            var commandText = NormalizeTerminalCommandText(rawCommand);
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return value;
            }

            var escapedWorkingDirectory = workingDirectory.Replace("'", "''", StringComparison.Ordinal);
            var updatedCommand = $"Set-Location -LiteralPath '{escapedWorkingDirectory}'; {commandText}".Trim();
            return $"{prefix} {QuoteArgument(updatedCommand)}".Trim();
        }

        private static string NormalizeWindowsTerminalArguments(string arguments)
        {
            var value = (arguments ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var hasKnownShell = value.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("cmd.exe", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf(" wsl", StringComparison.OrdinalIgnoreCase) >= 0
                || value.StartsWith("wsl", StringComparison.OrdinalIgnoreCase)
                || value.IndexOf(" bash", StringComparison.OrdinalIgnoreCase) >= 0
                || value.StartsWith("bash", StringComparison.OrdinalIgnoreCase);
            if (hasKnownShell)
            {
                return value;
            }

            var firstToken = ExtractFirstToken(value);
            if (LooksLikeWindowsTerminalDirective(firstToken))
            {
                return value;
            }

            var command = NormalizeTerminalCommandText(value);
            if (string.IsNullOrWhiteSpace(command))
            {
                return value;
            }

            var normalized = $"pwsh -NoExit -Command {QuoteArgument(command)}";
            AppLogger.LogInfo(
                $"WorkspaceRuntime: normalized terminal command-line. original='{value}', normalized='{normalized}'.");
            return normalized;
        }

        private static string NormalizeTerminalCommandText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            for (var i = 0; i < 4; i++)
            {
                var before = normalized;
                normalized = normalized.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();

                if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
                {
                    var inner = normalized.Substring(1, normalized.Length - 2).Trim();
                    if (ShouldUnwrapQuotedCommand(inner))
                    {
                        normalized = inner;
                    }
                }

                if (normalized.Length >= 2 && normalized[0] == '\'' && normalized[^1] == '\'')
                {
                    var inner = normalized.Substring(1, normalized.Length - 2).Trim();
                    if (ShouldUnwrapQuotedCommand(inner))
                    {
                        normalized = inner;
                    }
                }

                if (string.Equals(before, normalized, StringComparison.Ordinal))
                {
                    break;
                }
            }

            return normalized.Trim();
        }

        private static bool ShouldUnwrapQuotedCommand(string inner)
        {
            if (string.IsNullOrWhiteSpace(inner))
            {
                return true;
            }

            if (!inner.Any(char.IsWhiteSpace))
            {
                return true;
            }

            return !LooksLikeFilesystemPath(inner);
        }

        private static bool LooksLikeFilesystemPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains(Path.DirectorySeparatorChar)
                || value.Contains(Path.AltDirectorySeparatorChar)
                || value.Contains(":");
        }

        private static string ExtractFirstToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.TrimStart();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (trimmed[0] == '"')
            {
                var endQuote = trimmed.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    return trimmed.Substring(0, endQuote + 1);
                }
            }

            var firstSpace = trimmed.IndexOfAny(new[] { ' ', '\t' });
            return firstSpace < 0 ? trimmed : trimmed.Substring(0, firstSpace);
        }

        private static bool LooksLikeWindowsTerminalDirective(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(token, "new-tab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "nt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "split-pane", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "sp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "focus-tab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "ft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "move-focus", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "mf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "commandline", StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureVsCodeArguments(
            string arguments,
            string resolvedWorkingDirectory,
            ApplicationDefinition app)
        {
            var current = (arguments ?? string.Empty).Trim();
            if (!ContainsArgumentToken(current, "--new-window")
                && !ContainsArgumentToken(current, "-n"))
            {
                current = string.IsNullOrWhiteSpace(current)
                    ? "--new-window"
                    : $"--new-window {current}";
            }

            if (app?.LaunchNewIfUnbound == true)
            {
                if (!ContainsArgumentToken(current, "--skip-welcome"))
                {
                    current = string.IsNullOrWhiteSpace(current)
                        ? "--skip-welcome"
                        : $"{current} --skip-welcome";
                }

                if (!ContainsArgumentToken(current, "--user-data-dir"))
                {
                    var userDataDirectory = BuildVsCodeUserDataDirectory(app.Id);
                    if (!string.IsNullOrWhiteSpace(userDataDirectory))
                    {
                        current = string.IsNullOrWhiteSpace(current)
                            ? $"--user-data-dir {QuoteArgument(userDataDirectory)}"
                            : $"{current} --user-data-dir {QuoteArgument(userDataDirectory)}";
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedWorkingDirectory))
            {
                return current;
            }

            var hasRepoTarget = current.IndexOf(resolvedWorkingDirectory, StringComparison.OrdinalIgnoreCase) >= 0
                || current.IndexOf("{repo}", StringComparison.OrdinalIgnoreCase) >= 0
                || ContainsArgumentToken(current, "--folder-uri");
            if (!hasRepoTarget)
            {
                current = $"{current} {QuoteArgument(resolvedWorkingDirectory)}".Trim();
            }

            return current;
        }

        private static string BuildVsCodeUserDataDirectory(string appId)
        {
            var id = string.IsNullOrWhiteSpace(appId) ? "default" : appId.Trim();
            var safe = string.Concat(id.Where(ch =>
                (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '-'
                || ch == '_'));
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = "default";
            }

            try
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                var folderName = $"{safe}-{stamp}-{suffix}";
                var directory = Path.Combine(global::TopToolbar.AppPaths.ProfilesDirectory, "vscode", folderName);
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string EnsureExplorerArguments(string arguments, string resolvedWorkingDirectory)
        {
            var current = (arguments ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(resolvedWorkingDirectory))
            {
                current = QuoteArgument(resolvedWorkingDirectory);
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                return current;
            }

            if (!ContainsExplorerNewWindowSwitch(current))
            {
                current = $"/n, {current}";
            }

            return current;
        }

        private static bool ContainsExplorerNewWindowSwitch(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            var value = arguments.Trim();
            return value.StartsWith("/n", StringComparison.OrdinalIgnoreCase)
                || value.Contains(" /n", StringComparison.OrdinalIgnoreCase)
                || value.Contains(",/n", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsArgumentToken(string arguments, string token)
        {
            if (string.IsNullOrWhiteSpace(arguments) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return arguments.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase)
                || arguments.Equals(token, StringComparison.OrdinalIgnoreCase)
                || arguments.EndsWith(" " + token, StringComparison.OrdinalIgnoreCase)
                || arguments.Contains(" " + token + " ", StringComparison.OrdinalIgnoreCase);
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

        private static bool IsVsCodeApp(string path, string appName)
        {
            var normalizedPath = NormalizeProcessIdentity(path);
            var normalizedName = NormalizeProcessIdentity(appName);
            return string.Equals(normalizedPath, "code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedName, "code", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplorerApp(string path, string appName)
        {
            var normalizedPath = NormalizeProcessIdentity(path);
            var normalizedName = NormalizeProcessIdentity(appName);
            return string.Equals(normalizedPath, "explorer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedName, "explorer", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsDirectorySyntax(string value)
        {
            return value.Contains(Path.DirectorySeparatorChar)
                || value.Contains(Path.AltDirectorySeparatorChar)
                || value.Contains(":");
        }

        private static string ResolveExecutableFromPath(string executableName)
        {
            try
            {
                var pathValue = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrWhiteSpace(pathValue))
                {
                    return string.Empty;
                }

                var names = new List<string>();
                if (executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(executableName);
                }
                else
                {
                    names.Add(executableName + ".exe");
                    names.Add(executableName);
                }

                var dirs = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < dirs.Length; i++)
                {
                    var dir = dirs[i]?.Trim();
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }

                    for (var j = 0; j < names.Count; j++)
                    {
                        var candidate = Path.Combine(dir, names[j]);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ResolveExecutableFromAppPathsRegistry(string executableName)
        {
            try
            {
                var keyName = executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? executableName
                    : executableName + ".exe";
                var subKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{keyName}";

                var userValue = Registry.GetValue($@"HKEY_CURRENT_USER\{subKeyPath}", null, null) as string;
                if (!string.IsNullOrWhiteSpace(userValue) && File.Exists(userValue))
                {
                    return userValue;
                }

                var machineValue = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{subKeyPath}", null, null) as string;
                if (!string.IsNullOrWhiteSpace(machineValue) && File.Exists(machineValue))
                {
                    return machineValue;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ResolveExecutableFromKnownLocations(string executableName)
        {
            var normalized = NormalizeProcessIdentity(executableName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            if (string.Equals(normalized, "code", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
                };

                for (var i = 0; i < candidates.Length; i++)
                {
                    var candidate = candidates[i];
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return string.Empty;
        }

        private static string QuoteArgument(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var escaped = normalized.Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        }

        private static string DequoteArgument(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length >= 2
                && normalized[0] == '"'
                && normalized[^1] == '"')
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            return normalized.Replace("\\\"", "\"", StringComparison.Ordinal);
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
        [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            int ActivateApplication(string appUserModelId, string arguments, ActivateOptions options, out uint processId);

            int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);

            int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
        }

        [ComImport]
        [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
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
