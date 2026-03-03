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
        }

        private sealed class CreateWorkspaceInput
        {
            public string TemplateName { get; set; } = string.Empty;

            public string InstanceName { get; set; } = string.Empty;

            public string RepoRoot { get; set; } = string.Empty;
        }

        public ObservableCollection<QuickTemplateOption> QuickTemplates { get; } = new();

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
                await EnsureQuickTemplatesLoadedAsync().ConfigureAwait(true);

                if (QuickTemplates.Count == 0)
                {
                    await ShowSimpleMessageOnUiThreadAsync(
                        "No templates",
                        "No templates are available. Configure templates in Settings first.");
                    return;
                }

                var input = await PromptCreateWorkspaceAsync(this, QuickTemplates.ToList()).ConfigureAwait(true);
                if (input == null
                    || string.IsNullOrWhiteSpace(input.TemplateName)
                    || string.IsNullOrWhiteSpace(input.InstanceName))
                {
                    return;
                }

                try
                {
                    using var orchestrator = new WorkspaceTemplateOrchestrator();
                    var create = await orchestrator.CreateWorkspaceAsync(
                        new WorkspaceTemplateOrchestrator.CreateWorkspaceRequest
                        {
                            TemplateName = input.TemplateName,
                            InstanceName = input.InstanceName,
                            RepoRoot = input.RepoRoot,
                            NoLaunch = false,
                        },
                        CancellationToken.None).ConfigureAwait(false);

                    if (!create.Success)
                    {
                        await ShowSimpleMessageOnUiThreadAsync(
                            "Create workspace failed",
                            create.Message
                        );
                        return;
                    }

                    await ShowSimpleMessageOnUiThreadAsync(
                        "Workspace created",
                        create.Message);

                    var dispatcher = DispatcherQueue;
                    if (dispatcher != null && !dispatcher.HasThreadAccess)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        if (!dispatcher.TryEnqueue(async () =>
                        {
                            await RefreshWorkspaceGroupAsync();
                            tcs.TrySetResult(true);
                        }))
                        {
                            // Fallback, run synchronously if enqueue fails
                            await RefreshWorkspaceGroupAsync();
                        }
                        else
                        {
                            await tcs.Task.ConfigureAwait(true);
                        }
                    }
                    else
                    {
                        await RefreshWorkspaceGroupAsync();
                    }
                }
                catch (Exception ex)
                {
                    await ShowSimpleMessageOnUiThreadAsync("Create workspace failed", ex.Message);
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

        internal async Task EnsureQuickTemplatesLoadedAsync(bool forceReload = false)
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
                        Description = t.Description ?? string.Empty,
                        RequiresRepo = t.RequiresRepo,
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

        private void UpdateNewWorkspaceButtonState()
        {
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
                    ? "Create workspace from template"
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

            var instanceNameBox = new TextBox
            {
                PlaceholderText = "Workspace name",
                MinWidth = 280,
            };
            var templatePicker = new ComboBox
            {
                MinWidth = 280,
                ItemsSource = templates,
                DisplayMemberPath = nameof(QuickTemplateOption.DisplayName),
                PlaceholderText = "Select template",
                SelectedIndex = 0,
            };
            var repoRootBox = new TextBox
            {
                PlaceholderText = "Repository path (optional)",
                MinWidth = 280,
            };
            var descriptionBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(templatePicker);
            content.Children.Add(descriptionBlock);
            content.Children.Add(instanceNameBox);
            content.Children.Add(repoRootBox);

            var dialog = new ContentDialog
            {
                XamlRoot = overlay.Root.XamlRoot,
                Title = "Create workspace",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = content,
            };

            void UpdateEnabled()
            {
                var selectedTemplate = templatePicker.SelectedItem as QuickTemplateOption
                    ?? templates.FirstOrDefault();
                var hasName = !string.IsNullOrWhiteSpace(instanceNameBox.Text);
                var hasRepo = !string.IsNullOrWhiteSpace(repoRootBox.Text);
                var repoValid = selectedTemplate?.RequiresRepo != true || hasRepo;
                dialog.IsPrimaryButtonEnabled = hasName && repoValid;

                if (selectedTemplate == null)
                {
                    descriptionBlock.Text = "No template selected.";
                    repoRootBox.PlaceholderText = "Repository path";
                }
                else
                {
                    descriptionBlock.Text = string.IsNullOrWhiteSpace(selectedTemplate.Description)
                        ? $"Template: {selectedTemplate.DisplayName}"
                        : selectedTemplate.Description;
                    repoRootBox.PlaceholderText = selectedTemplate.RequiresRepo
                        ? "Repository path (required)"
                        : "Repository path (optional)";
                }
            }

            dialog.IsPrimaryButtonEnabled = false;
            templatePicker.SelectionChanged += (_, __) => UpdateEnabled();
            instanceNameBox.TextChanged += (_, __) => UpdateEnabled();
            repoRootBox.TextChanged += (_, __) => UpdateEnabled();
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
                InstanceName = instanceNameBox.Text?.Trim() ?? string.Empty,
                RepoRoot = repoRootBox.Text?.Trim() ?? string.Empty,
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
            var dispatcher = DispatcherQueue;
            if (dispatcher == null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            if (dispatcher.HasThreadAccess)
            {
                return ShowSimpleMessageAsync(this, title, message);
            }

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await ShowSimpleMessageAsync(this, title, message);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            return tcs.Task;
        }

        private static async System.Threading.Tasks.Task ShowSimpleMessageAsync(WindowEx owner, string title, string message)
        {
            using var overlay = await TransparentOverlayHost.CreateAsync(owner).ConfigureAwait(true);
            if (overlay == null)
            {
                return;
            }

            using var overlayScope = ContentDialogOverlayScope.Transparent();

            var dialog = new ContentDialog
            {
                XamlRoot = overlay.Root.XamlRoot,
                Title = title ?? string.Empty,
                Content = new TextBlock
                {
                    Text = message ?? string.Empty,
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync(ContentDialogPlacement.Popup);
        }
    }
}
