// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TopToolbar.Logging;
using TopToolbar.Providers;
using TopToolbar.Services.Workspaces;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private async System.Threading.Tasks.Task HandleQuickSnapshotAsync(Button triggerButton, SnapshotFlightOrigin origin = SnapshotFlightOrigin.ToolbarButton)
        {
            if (_snapshotInProgress)
            {
                return;
            }

            _snapshotInProgress = true;
            await SetButtonEnabledAsync(triggerButton, false).ConfigureAwait(true);
            UpdateSnapshotButtonState();

            BeginSnapshotFlight(origin);

            try
            {
                var defaultSnapshotName = await WorkspaceNameSuggester
                    .GetNextWorkspaceNameAsync(CancellationToken.None)
                    .ConfigureAwait(true);
                AppLogger.LogInfo($"QuickSnapshot: prompt open, defaultName='{defaultSnapshotName}'.");
                var snapshotName = await _toastWindow
                    .ShowInputPromptAsync(
                        "Snapshot workspace",
                        "Enter a name for this workspace snapshot.",
                        "Workspace name",
                        defaultSnapshotName,
                        fieldLabel: "Workspace name",
                        confirmButtonText: "Save snapshot",
                        subtitle: "Save current desktop as a runtime workspace.")
                    .ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(snapshotName))
                {
                    AppLogger.LogInfo("QuickSnapshot: canceled by user.");
                    _notificationService.ShowInfo("Snapshot canceled.");
                    return;
                }

                AppLogger.LogInfo($"QuickSnapshot: confirmed name='{snapshotName}'.");
                using var provider = new WorkspaceProvider();
                var workspace = await provider.SnapshotAsync(snapshotName, CancellationToken.None).ConfigureAwait(true);
                if (workspace == null)
                {
                    AppLogger.LogWarning("QuickSnapshot: provider returned null workspace.");
                    await ShowSimpleMessageOnUiThreadAsync(
                        "Snapshot failed",
                        "No eligible windows were detected to capture.");
                    return;
                }

                AppLogger.LogInfo($"QuickSnapshot: saved workspace id='{workspace.Id}', name='{workspace.Name}'.");
                await ShowSimpleMessageOnUiThreadAsync(
                    "Snapshot saved",
                    $"Workspace '{workspace.Name}' has been captured.");

                await RefreshWorkspaceGroupAsync().ConfigureAwait(true);

                await CompleteSnapshotFlightAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("QuickSnapshot: exception during snapshot.", ex);
                await ShowSimpleMessageOnUiThreadAsync("Snapshot failed", ex.Message);
            }
            finally
            {
                DiscardPendingSnapshotFlight();
                await SetButtonEnabledAsync(triggerButton, true).ConfigureAwait(true);
                _snapshotInProgress = false;
                UpdateSnapshotButtonState();
            }
        }

        private void UpdateSnapshotButtonState()
        {
            if (SnapshotButton != null)
            {
                var snapshotEnabled = !_snapshotInProgress;
                SnapshotButton.IsEnabled = snapshotEnabled;
                SnapshotButton.Opacity = snapshotEnabled ? 1d : 0.45d;
                if (SnapshotLabel != null)
                {
                    SnapshotLabel.Opacity = snapshotEnabled ? 1d : 0.45d;
                }
            }
        }

        private System.Threading.Tasks.Task SetButtonEnabledAsync(Button btn, bool enabled)
        {
            if (btn == null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var dispatcher = btn.DispatcherQueue ?? DispatcherQueue;

            void Apply()
            {
                try
                {
                    // Only touch UI element if it is still loaded/attached
                    if (btn.IsLoaded)
                    {
                        btn.IsEnabled = enabled;
                    }
                }
                catch
                {
                    // Control may have been disposed/recycled during UI rebuild; ignore
                }
            }

            if (dispatcher == null)
            {
                Apply();
                return System.Threading.Tasks.Task.CompletedTask;
            }

            if (dispatcher.HasThreadAccess)
            {
                Apply();
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            if (!dispatcher.TryEnqueue(() =>
            {
                Apply();
                tcs.TrySetResult(true);
            }))
            {
                // Fallback: apply directly if enqueue fails
                Apply();
                return System.Threading.Tasks.Task.CompletedTask;
            }

            return tcs.Task;
        }

        private System.Threading.Tasks.Task ShowSimpleMessageOnUiThreadAsync(string title, string message)
        {
            var normalizedTitle = (title ?? string.Empty).Trim();
            var normalizedMessage = (message ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedMessage))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var fullMessage = string.IsNullOrWhiteSpace(normalizedTitle)
                ? normalizedMessage
                : $"{normalizedTitle}: {normalizedMessage}";

            if (normalizedTitle.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedTitle.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _notificationService.ShowError(fullMessage);
            }
            else if (normalizedTitle.IndexOf("saved", StringComparison.OrdinalIgnoreCase) >= 0
                     || normalizedTitle.IndexOf("created", StringComparison.OrdinalIgnoreCase) >= 0
                     || normalizedTitle.IndexOf("deleted", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _notificationService.ShowSuccess(fullMessage);
            }
            else
            {
                _notificationService.ShowInfo(fullMessage);
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
