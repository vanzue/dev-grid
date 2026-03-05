// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TopToolbar.Logging;
using TopToolbar.Providers;
using TopToolbar.Services.Workspaces;
using WinUIEx;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        public sealed class QuickTemplateOption
        {
            public string Name { get; set; } = string.Empty;

            public string DisplayName { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;

            public bool RequiresRepo { get; set; }

            public bool CreateWorktreeByDefault { get; set; }

            public string WorktreeBaseBranch { get; set; } = "main";

            public string DefaultRepoRoot { get; set; } = string.Empty;
        }

        private sealed class CreateWorkspaceInput
        {
            public string TemplateName { get; set; } = string.Empty;
        }

        public ObservableCollection<QuickTemplateOption> QuickTemplates { get; } = new();
        private readonly SemaphoreSlim _quickTemplateLoadGate = new(1, 1);
        private Task _quickTemplateWarmupTask = Task.CompletedTask;

        private async System.Threading.Tasks.Task HandleSnapshotButtonClickAsync(Button triggerButton)
        {
            if (_snapshotInProgress)
            {
                return;
            }

            _snapshotInProgress = true;
            await SetButtonEnabledAsync(triggerButton, false).ConfigureAwait(true);

            try
            {
                await EnsureQuickTemplatesLoadedAsync(forceReload: false).ConfigureAwait(true);

                if (QuickTemplates.Count == 0)
                {
                    await ShowSimpleMessageOnUiThreadAsync(
                        "No templates",
                        "No templates are available. Configure templates in Settings first.");
                    return;
                }

                var input = await PromptCreateWorkspaceAsync(this, QuickTemplates.ToList()).ConfigureAwait(true);
                if (input == null
                    || string.IsNullOrWhiteSpace(input.TemplateName))
                {
                    return;
                }

                Guid progressId = Guid.Empty;
                try
                {
                    progressId = _notificationService.ShowProgress("Preparing window set...");
                    var progress = new Progress<string>(message =>
                    {
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            return;
                        }

                        _notificationService.UpdateProgress(progressId, message);
                    });

                    using var orchestrator = new WorkspaceTemplateOrchestrator();
                    var create = await orchestrator.CreateWorkspaceAsync(
                        new WorkspaceTemplateOrchestrator.CreateWorkspaceRequest
                        {
                            TemplateName = input.TemplateName,
                            NoLaunch = false,
                            EphemeralLaunch = true,
                            Progress = progress,
                        },
                        CancellationToken.None).ConfigureAwait(false);

                    if (!create.Success)
                    {
                        _notificationService.CompleteProgress(
                            progressId,
                            $"Launch failed: {create.Message}",
                            isError: true);
                        return;
                    }

                    _notificationService.CompleteProgress(progressId, $"Window set launched: {create.Message}");
                }
                catch (Exception ex)
                {
                    if (progressId != Guid.Empty)
                    {
                        _notificationService.CompleteProgress(progressId, $"Launch failed: {ex.Message}", isError: true);
                    }
                    else
                    {
                        await ShowSimpleMessageOnUiThreadAsync("Launch failed", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                await SetButtonEnabledAsync(triggerButton, true).ConfigureAwait(true);
                _snapshotInProgress = false;
                UpdateNewWorkspaceButtonState();
            }
        }

        private async System.Threading.Tasks.Task HandleQuickSnapshotAsync(Button triggerButton)
        {
            if (_snapshotInProgress)
            {
                return;
            }

            _snapshotInProgress = true;
            await SetButtonEnabledAsync(triggerButton, false).ConfigureAwait(true);
            UpdateNewWorkspaceButtonState();

            try
            {
                var defaultSnapshotName = $"Snapshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
                AppLogger.LogInfo($"QuickSnapshot: prompt open, defaultName='{defaultSnapshotName}'.");
                var snapshotName = await _toastWindow
                    .ShowInputPromptAsync(
                        "Snapshot workspace",
                        "Enter a name for this workspace snapshot.",
                        "Workspace name",
                        defaultSnapshotName)
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
            }
            catch (Exception ex)
            {
                AppLogger.LogError("QuickSnapshot: exception during snapshot.", ex);
                await ShowSimpleMessageOnUiThreadAsync("Snapshot failed", ex.Message);
            }
            finally
            {
                await SetButtonEnabledAsync(triggerButton, true).ConfigureAwait(true);
                _snapshotInProgress = false;
                UpdateNewWorkspaceButtonState();
            }
        }

        internal async Task EnsureQuickTemplatesLoadedAsync(bool forceReload = false)
        {
            if (!forceReload && QuickTemplates.Count > 0)
            {
                return;
            }

            await _quickTemplateLoadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!forceReload && QuickTemplates.Count > 0)
                {
                    return;
                }

                IReadOnlyList<QuickTemplateOption> templates = Array.Empty<QuickTemplateOption>();
                try
                {
                    using var orchestrator = new WorkspaceTemplateOrchestrator();
                    var list = await orchestrator.ListTemplatesAsync(CancellationToken.None).ConfigureAwait(false);
                    templates = list
                        .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name))
                        .Select(t => new QuickTemplateOption
                        {
                            Name = t.Name,
                            DisplayName = string.IsNullOrWhiteSpace(t.DisplayName) ? t.Name : t.DisplayName,
                            RequiresRepo = t.RequiresRepo,
                            CreateWorktreeByDefault = t.Creation?.CreateWorktreeByDefault ?? false,
                            WorktreeBaseBranch = string.IsNullOrWhiteSpace(t.Creation?.WorktreeBaseBranch)
                                ? "main"
                                : t.Creation.WorktreeBaseBranch,
                            DefaultRepoRoot = t.DefaultRepoRoot ?? string.Empty,
                        })
                        .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch
                {
                    templates = Array.Empty<QuickTemplateOption>();
                }

                await RunOnUiThreadAsync(() =>
                {
                    QuickTemplates.Clear();
                    foreach (var template in templates)
                    {
                        QuickTemplates.Add(template);
                    }
                    UpdateNewWorkspaceButtonState();
                }).ConfigureAwait(true);
            }
            finally
            {
                _quickTemplateLoadGate.Release();
            }
        }

        internal void WarmQuickTemplatesInBackground(bool forceReload = false)
        {
            if (!forceReload && _quickTemplateWarmupTask != null && !_quickTemplateWarmupTask.IsCompleted)
            {
                return;
            }

            var warmup = EnsureQuickTemplatesLoadedAsync(forceReload);
            _quickTemplateWarmupTask = warmup;
            _ = warmup.ContinueWith(
                _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void UpdateNewWorkspaceButtonState()
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

            if (NewWorkspaceButton == null)
            {
                return;
            }

            var enabled = QuickTemplates.Count > 0 && !_snapshotInProgress;
            NewWorkspaceButton.IsEnabled = enabled;
            NewWorkspaceButton.Opacity = enabled ? 1d : 0.45d;
            ToolTipService.SetToolTip(
                NewWorkspaceButton,
                enabled
                    ? "Launch a window set from template"
                    : "Configure templates in Settings first");
            if (NewWorkspaceLabel != null)
            {
                NewWorkspaceLabel.Opacity = enabled ? 1d : 0.45d;
            }
        }

        private static async Task<CreateWorkspaceInput> PromptCreateWorkspaceAsync(
            WindowEx owner,
            IReadOnlyList<QuickTemplateOption> templates)
        {
            if (templates == null || templates.Count == 0)
            {
                return null;
            }

            using var overlay = await TransparentOverlayHost.CreateAsync(owner).ConfigureAwait(true);
            if (overlay == null)
            {
                return null;
            }

            using var overlayScope = ContentDialogOverlayScope.Transparent();

            var templatePicker = new ComboBox
            {
                MinWidth = 280,
                ItemsSource = templates,
                DisplayMemberPath = nameof(QuickTemplateOption.DisplayName),
                PlaceholderText = "Select template",
                SelectedIndex = 0,
            };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(templatePicker);

            var dialog = new ContentDialog
            {
                XamlRoot = overlay.Root.XamlRoot,
                Title = "Launch window set",
                PrimaryButtonText = "Launch",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = content,
            };

            void UpdateEnabled()
            {
                var selectedTemplate = templatePicker.SelectedItem as QuickTemplateOption
                    ?? templates.FirstOrDefault();
                dialog.IsPrimaryButtonEnabled = selectedTemplate != null;
            }

            dialog.IsPrimaryButtonEnabled = false;
            templatePicker.SelectionChanged += (_, __) => UpdateEnabled();

            UpdateEnabled();

            var result = await dialog.ShowAsync(ContentDialogPlacement.Popup);
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            var selected = templatePicker.SelectedItem as QuickTemplateOption
                ?? templates.FirstOrDefault();
            if (selected == null || string.IsNullOrWhiteSpace(selected.Name))
            {
                return null;
            }

            return new CreateWorkspaceInput
            {
                TemplateName = selected.Name,
            };
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
