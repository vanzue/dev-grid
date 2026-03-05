// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Display;
using TopToolbar.Services.Providers;
using TopToolbar.Services.Windowing;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspacesRuntimeService : IDisposable
    {
        private readonly WorkspaceProviderConfigStore _configStore;
        private readonly WorkspaceDefinitionStore _definitionStore;
        private readonly DisplayManager _displayManager;
        private readonly WindowManager _windowManager;
        private readonly ManagedWindowRegistry _managedWindows;
        private readonly WorkspaceSnapshotter _snapshotter;
        private readonly WorkspaceLauncher _launcher;
        private bool _disposed;

        public WorkspacesRuntimeService(string workspacesPath = null)
        {
            _configStore = new WorkspaceProviderConfigStore(workspacesPath);
            _definitionStore = new WorkspaceDefinitionStore(null, _configStore);
            _displayManager = new DisplayManager();
            _windowManager = new WindowManager(_displayManager);
            _managedWindows = WindowClaimer.Instance.Registry;
            _snapshotter = new WorkspaceSnapshotter(_definitionStore, _windowManager, _displayManager, _managedWindows);
            _launcher = new WorkspaceLauncher(_definitionStore, _windowManager, _managedWindows, _displayManager);

            // Subscribe to window destruction events to keep ManagedWindowRegistry in sync
            _windowManager.WindowDestroyed += OnWindowDestroyed;
        }

        private void OnWindowDestroyed(IntPtr hwnd)
        {
            // When a window is destroyed, remove it from the managed registry
            var boundAppIds = _managedWindows.GetBoundAppIds(hwnd);
            if (boundAppIds.Count > 0)
            {
                var entries = new List<string>(boundAppIds.Count);
                foreach (var appId in boundAppIds)
                {
                    var workspaceId = _managedWindows.GetWorkspaceIdForApp(appId) ?? "<unknown>";
                    entries.Add($"{appId}@{workspaceId}");
                }

                AppLogger.LogInfo($"WorkspaceRuntime: WindowDestroyed - unbinding handle={hwnd} apps=[{string.Join(", ", entries)}]");
            }

            _managedWindows.UnbindWindow(hwnd);
        }

        public Task<WorkspaceDefinition> SnapshotAsync(
            string workspaceName,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspacesRuntimeService));
            return _snapshotter.SnapshotAsync(workspaceName, cancellationToken);
        }

        public async Task<bool> LaunchWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken,
            IProgress<string> progress = null,
            bool allowLaunchMissingWindows = true)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspacesRuntimeService));
            var result = await _launcher.LaunchWorkspaceAsync(workspaceId, cancellationToken, progress, allowLaunchMissingWindows).ConfigureAwait(false);
            return result != null && result.Ok;
        }

        public async Task<bool> LaunchWorkspaceAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken,
            IProgress<string> progress = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspacesRuntimeService));
            var result = await _launcher.LaunchWorkspaceAsync(workspace, cancellationToken, progress).ConfigureAwait(false);
            return result != null && result.Ok;
        }

        public Task<WorkspaceSwitchDiagnostics> LaunchWorkspaceDetailedAsync(
            string workspaceId,
            CancellationToken cancellationToken,
            bool allowLaunchMissingWindows = true)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspacesRuntimeService));
            return _launcher.LaunchWorkspaceAsync(workspaceId, cancellationToken, progress: null, allowLaunchMissingWindows);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _windowManager.WindowDestroyed -= OnWindowDestroyed;
            _windowManager.Dispose();
            _displayManager.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
