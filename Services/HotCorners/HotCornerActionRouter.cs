// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
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
            _notifications.ShowInfo($"Workspace '{workspace.Name}' captured.");

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
    }
}
