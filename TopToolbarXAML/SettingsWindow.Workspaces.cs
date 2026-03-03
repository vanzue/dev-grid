// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Providers;
using TopToolbar.Serialization;
using TopToolbar.Services.Workspaces;
using TopToolbar.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TopToolbar
{
    public sealed partial class SettingsWindow
    {
        private sealed record TemplateChoice(string Name, string DisplayName, string Description, int WindowCount);

        private async void OnRemoveWorkspace(object sender, RoutedEventArgs e)
        {
            var context = (sender as FrameworkElement)?.DataContext as WorkspaceButtonViewModel;
            var workspace = context ?? _vm.SelectedWorkspace;
            if (workspace == null)
            {
                return;
            }

            _vm.RemoveWorkspace(workspace);
            await _vm.SaveAsync();
        }

        private async void OnBrowseWorkspaceIcon(object sender, RoutedEventArgs e)
        {
            var workspace =
                (sender as FrameworkElement)?.DataContext as WorkspaceButtonViewModel
                ?? _vm.SelectedWorkspace;
            if (workspace == null)
            {
                return;
            }

            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".ico");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            await _vm.TrySetWorkspaceImageIconFromFileAsync(workspace, file.Path)
                .ConfigureAwait(true);
        }

        private void OnResetWorkspaceIcon(object sender, RoutedEventArgs e)
        {
            var workspace =
                (sender as FrameworkElement)?.DataContext as WorkspaceButtonViewModel
                ?? _vm.SelectedWorkspace;
            if (workspace == null)
            {
                return;
            }

            _vm.ResetWorkspaceIcon(workspace);
        }

        private void OnAddWorkspaceApp(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedWorkspace == null)
            {
                return;
            }

            _vm.AddWorkspaceApp(_vm.SelectedWorkspace);
        }

        private async void OnRemoveWorkspaceApp(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedWorkspace == null)
            {
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is ApplicationDefinition app)
            {
                _vm.RemoveWorkspaceApp(_vm.SelectedWorkspace, app);
                await _vm.SaveAsync();
            }
        }

        private static async Task ShowSimpleMessageAsync(
            XamlRoot xamlRoot,
            string title,
            string message
        )
        {
            if (xamlRoot == null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title ?? string.Empty,
                Content = new TextBlock
                {
                    Text = message ?? string.Empty,
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync();
        }

        private async void OnSnapshotWorkspace(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isClosed)
            {
                return;
            }

            if (Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            var nameBox = new TextBox { PlaceholderText = "Workspace name" };

            if (
                root.Resources != null
                && root.Resources.TryGetValue("StandardTextBoxStyle", out var styleObj)
                && styleObj is Style textBoxStyle
            )
            {
                nameBox.Style = textBoxStyle;
            }

            var dialogContent = new StackPanel { Spacing = 12 };
            dialogContent.Children.Add(
                new TextBlock
                {
                    Text = "Enter a name for the new workspace snapshot.",
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            dialogContent.Children.Add(nameBox);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Create workspace snapshot",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = dialogContent,
                IsPrimaryButtonEnabled = false,
            };

            nameBox.TextChanged += (_, __) =>
            {
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var workspaceName = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(workspaceName))
            {
                return;
            }

            try
            {
                WorkspaceProvider workspaceProvider = null;
                if (_providerRuntime != null
                    && _providerRuntime.TryGetProvider("WorkspaceProvider", out var provider))
                {
                    workspaceProvider = provider as WorkspaceProvider;
                }

                if (workspaceProvider == null)
                {
                    await ShowSimpleMessageOnUiThreadAsync(
                        xamlRoot,
                        "Snapshot failed",
                        "Workspace provider is not available."
                    );
                    return;
                }

                var workspace = await workspaceProvider
                    .SnapshotAsync(workspaceName, CancellationToken.None)
                    .ConfigureAwait(true);
                if (workspace == null)
                {
                    await ShowSimpleMessageOnUiThreadAsync(
                        xamlRoot,
                        "Snapshot failed",
                        "No eligible windows were detected to capture."
                    );
                    return;
                }

                await _vm.RefreshWorkspacesAsync().ConfigureAwait(true);

                await ShowSimpleMessageOnUiThreadAsync(
                    xamlRoot,
                    "Snapshot saved",
                    $"Workspace \"{workspace.Name}\" has been saved."
                );
            }
            catch (Exception ex)
            {
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Snapshot failed", ex.Message);
            }
        }

        private async void OnCreateWorkspaceFromTemplate(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isClosed || Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var templateDefinitions = (await orchestrator.ListTemplatesAsync(CancellationToken.None).ConfigureAwait(true))
                    .Where(template => template != null)
                    .ToList();
                var templates = templateDefinitions
                    .Where(template => template != null)
                    .Select(template => new TemplateChoice(
                        template.Name,
                        string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName,
                        template.Description ?? string.Empty,
                        template.Windows?.Count ?? 0))
                    .OrderBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (templates.Count == 0)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "No templates", "No workspace templates were found. Use 'Create template' first.");
                    return;
                }

                var templatePicker = new ComboBox
                {
                    ItemsSource = templates,
                    DisplayMemberPath = nameof(TemplateChoice.DisplayName),
                    SelectedIndex = 0,
                    MinWidth = 360,
                };
                var instanceNameBox = new TextBox
                {
                    PlaceholderText = "Workspace instance name",
                };
                var repoPathBox = new TextBox
                {
                    PlaceholderText = "Repository path (optional)",
                };
                var launchToggle = new ToggleSwitch
                {
                    Header = "Launch after create",
                    IsOn = true,
                };

                if (
                    root.Resources != null
                    && root.Resources.TryGetValue("StandardTextBoxStyle", out var styleObj)
                    && styleObj is Style textBoxStyle
                )
                {
                    instanceNameBox.Style = textBoxStyle;
                    repoPathBox.Style = textBoxStyle;
                }

                var dialogContent = new StackPanel { Spacing = 12 };
                dialogContent.Children.Add(new TextBlock { Text = "Template" });
                dialogContent.Children.Add(templatePicker);
                dialogContent.Children.Add(new TextBlock { Text = "Instance name" });
                dialogContent.Children.Add(instanceNameBox);
                dialogContent.Children.Add(new TextBlock { Text = "Repository path" });
                dialogContent.Children.Add(repoPathBox);
                dialogContent.Children.Add(launchToggle);

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "Create workspace from template",
                    PrimaryButtonText = "Create",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = dialogContent,
                    IsPrimaryButtonEnabled = false,
                };

                void UpdateCreateEnabled()
                {
                    var selected = templatePicker.SelectedItem as TemplateChoice;
                    var requiresRepo = selected != null && TemplateRequiresRepo(templateDefinitions, selected.Name);
                    if (requiresRepo)
                    {
                        repoPathBox.PlaceholderText = "Repository path (required)";
                    }
                    else
                    {
                        repoPathBox.PlaceholderText = "Repository path (optional)";
                    }

                    dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(instanceNameBox.Text)
                        && selected != null
                        && (!requiresRepo || !string.IsNullOrWhiteSpace(repoPathBox.Text));
                }

                instanceNameBox.TextChanged += (_, __) => UpdateCreateEnabled();
                repoPathBox.TextChanged += (_, __) => UpdateCreateEnabled();

                templatePicker.SelectionChanged += (_, __) => UpdateCreateEnabled();
                UpdateCreateEnabled();

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                var choice = templatePicker.SelectedItem as TemplateChoice;
                if (choice == null)
                {
                    return;
                }

                var createResult = await orchestrator.CreateWorkspaceAsync(
                    new WorkspaceTemplateOrchestrator.CreateWorkspaceRequest
                    {
                        TemplateName = choice.Name,
                        InstanceName = instanceNameBox.Text?.Trim() ?? string.Empty,
                        RepoRoot = repoPathBox.Text?.Trim() ?? string.Empty,
                        NoLaunch = !launchToggle.IsOn,
                    },
                    CancellationToken.None).ConfigureAwait(true);

                if (!createResult.Success)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Create failed", createResult.Message);
                    return;
                }

                await _vm.RefreshWorkspacesAsync().ConfigureAwait(true);
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Workspace created", createResult.Message);
            }
            catch (Exception ex)
            {
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Create failed", ex.Message);
            }
        }

        private static bool TemplateRequiresRepo(
            IReadOnlyList<TemplateDefinition> templates,
            string templateName)
        {
            if (templates == null || string.IsNullOrWhiteSpace(templateName))
            {
                return false;
            }

            var match = templates.FirstOrDefault(template =>
                template != null
                && string.Equals(template.Name, templateName, StringComparison.OrdinalIgnoreCase));
            return match?.RequiresRepo ?? false;
        }

        private async void OnCreateWorkspaceTemplate(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isClosed || Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;

            var templateNameBox = new TextBox
            {
                PlaceholderText = "template-id (a-z0-9-, 3-64 chars)",
                MinWidth = 360,
            };
            var displayNameBox = new TextBox
            {
                PlaceholderText = "Display name",
                MinWidth = 360,
            };
            var descriptionBox = new TextBox
            {
                PlaceholderText = "Description (optional)",
                MinWidth = 360,
            };
            var requiresRepoToggle = new ToggleSwitch
            {
                Header = "Requires repository path",
                IsOn = false,
            };
            var hintBlock = new TextBlock
            {
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "Create a starter template, then refine it in Configure templates.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(templateNameBox);
            content.Children.Add(displayNameBox);
            content.Children.Add(descriptionBox);
            content.Children.Add(requiresRepoToggle);
            content.Children.Add(hintBlock);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Create template",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = content,
                IsPrimaryButtonEnabled = false,
            };

            void UpdateState()
            {
                var normalizedName = WorkspaceStoragePaths.NormalizeTemplateName(templateNameBox.Text);
                var isValidName = IsValidTemplateName(normalizedName);

                hintBlock.Text = isValidName
                    ? $"Template id: {normalizedName}"
                    : "Template id must match: a-z, 0-9, hyphen, length 3-64.";

                dialog.IsPrimaryButtonEnabled = isValidName;
            }

            templateNameBox.TextChanged += (_, __) => UpdateState();
            UpdateState();

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var normalizedTemplateName = WorkspaceStoragePaths.NormalizeTemplateName(templateNameBox.Text);
            if (!IsValidTemplateName(normalizedTemplateName))
            {
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Invalid template id", "Template id must match a-z0-9- and be 3-64 characters.");
                return;
            }

            var displayName = string.IsNullOrWhiteSpace(displayNameBox.Text)
                ? BuildDisplayNameFromTemplateName(normalizedTemplateName)
                : displayNameBox.Text.Trim();
            var description = descriptionBox.Text?.Trim() ?? string.Empty;

            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var existing = await orchestrator.GetTemplateAsync(normalizedTemplateName, CancellationToken.None).ConfigureAwait(true);
                if (existing != null)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template exists", $"Template '{normalizedTemplateName}' already exists.");
                    return;
                }

                var template = BuildStarterTemplate(
                    normalizedTemplateName,
                    displayName,
                    description,
                    requiresRepoToggle.IsOn);

                await orchestrator.SaveTemplateAsync(template, CancellationToken.None).ConfigureAwait(true);
                await ShowSimpleMessageOnUiThreadAsync(
                    xamlRoot,
                    "Template created",
                    $"Template '{normalizedTemplateName}' was created. Use Configure templates to customize it.");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: create template failed.", ex);
                try
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Create template failed", BuildExceptionDisplayMessage(ex));
                }
                catch
                {
                }
            }
        }

        private Task ShowSimpleMessageOnUiThreadAsync(
            XamlRoot xamlRoot,
            string title,
            string message)
        {
            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                return ShowSimpleMessageAsync(xamlRoot, title, message);
            }

            var tcs = new TaskCompletionSource<bool>();
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await ShowSimpleMessageAsync(xamlRoot, title, message);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetResult(true);
            }

            return tcs.Task;
        }

        private static bool IsValidTemplateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name.Length > 64)
            {
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                var isLower = ch >= 'a' && ch <= 'z';
                var isDigit = ch >= '0' && ch <= '9';
                if (!isLower && !isDigit && ch != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildDisplayNameFromTemplateName(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return "New Template";
            }

            var parts = templateName.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return "New Template";
            }

            return string.Join(" ", parts.Select(part =>
                part.Length == 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part.Substring(1)));
        }

        private static TemplateDefinition BuildStarterTemplate(
            string templateName,
            string displayName,
            string description,
            bool requiresRepo)
        {
            var role = "main";

            return new TemplateDefinition
            {
                SchemaVersion = 1,
                Name = templateName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? templateName : displayName,
                Description = description ?? string.Empty,
                RequiresRepo = requiresRepo,
                DefaultRepoRoot = string.Empty,
                FocusPriority = new List<string> { role },
                Layout = new TemplateLayoutDefinition
                {
                    Strategy = "single",
                    MonitorPolicy = "primary",
                    Preset = "single",
                    Slots = new List<TemplateLayoutSlotDefinition>
                    {
                        new()
                        {
                            Role = role,
                            X = 0,
                            Y = 0,
                            Width = 1,
                            Height = 1,
                        },
                    },
                },
                Windows = new List<TemplateWindowDefinition>
                {
                    new()
                    {
                        Role = role,
                        Exe = "notepad.exe",
                        WorkingDirectory = requiresRepo ? "{repo}" : string.Empty,
                        Args = string.Empty,
                        Init = string.Empty,
                        Monitor = "primary",
                        MatchHints = new TemplateMatchHints
                        {
                            ProcessName = "notepad",
                        },
                    },
                },
            };
        }

        private async void OnManageWorkspaceTemplates(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isClosed || Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var templates = await orchestrator.ListTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
                var choices = new System.Collections.ObjectModel.ObservableCollection<TemplateChoice>(templates
                    .Where(template => template != null)
                    .Select(template => new TemplateChoice(
                        template.Name,
                        string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName,
                        template.Description ?? string.Empty,
                        template.Windows?.Count ?? 0))
                    .OrderBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList());

                if (choices.Count == 0)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "No templates", "No workspace templates were found. Use 'Create template' first.");
                    return;
                }

                var templateList = new ListView
                {
                    ItemsSource = choices,
                    DisplayMemberPath = nameof(TemplateChoice.DisplayName),
                    SelectionMode = ListViewSelectionMode.Single,
                    MinWidth = 360,
                    MinHeight = 180,
                    MaxHeight = 260,
                };
                var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };

                void UpdateSummary()
                {
                    if (templateList.SelectedItem is not TemplateChoice selected)
                    {
                        summary.Text = string.Empty;
                        return;
                    }

                    var description = string.IsNullOrWhiteSpace(selected.Description) ? "-" : selected.Description;
                    summary.Text =
                        $"Name: {selected.Name}\n"
                        + $"Display: {selected.DisplayName}\n"
                        + $"Windows: {selected.WindowCount}\n"
                        + $"Description: {description}";
                }

                UpdateSummary();
                templateList.SelectionChanged += (_, __) => UpdateSummary();

                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(new TextBlock
                {
                    Text = "Select a template to configure.",
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(templateList);
                content.Children.Add(summary);

                templateList.SelectedIndex = 0;

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "Configure templates",
                    PrimaryButtonText = "Configure",
                    SecondaryButtonText = "Delete template",
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = content,
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.None)
                {
                    return;
                }

                var selected = templateList.SelectedItem as TemplateChoice;
                if (selected == null)
                {
                    return;
                }

                if (result == ContentDialogResult.Primary)
                {
                    var template = await orchestrator.GetTemplateAsync(selected.Name, CancellationToken.None).ConfigureAwait(true);
                    if (template == null)
                    {
                        await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template missing", $"Template '{selected.Name}' no longer exists.");
                        return;
                    }

                    _ = await ShowTemplateDesignerAsync(xamlRoot, template, orchestrator).ConfigureAwait(true);
                    return;
                }

                var confirmDialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "Delete template",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = $"Delete template '{selected.Name}'?",
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

                var confirm = await confirmDialog.ShowAsync();
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }

                var deleted = await orchestrator.DeleteTemplateAsync(selected.Name, CancellationToken.None).ConfigureAwait(true);
                if (!deleted)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Delete failed", $"Template '{selected.Name}' was not deleted.");
                    return;
                }

                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template deleted", $"Template '{selected.Name}' was deleted.");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: template management failed.", ex);
                try
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template management failed", BuildExceptionDisplayMessage(ex));
                }
                catch (Exception displayEx)
                {
                    AppLogger.LogError("SettingsWindow: failed to display template management error.", displayEx);
                }
            }
        }

        private static string BuildExceptionDisplayMessage(Exception ex)
        {
            if (ex == null)
            {
                return "Unknown error.";
            }

            var message = string.IsNullOrWhiteSpace(ex.Message)
                ? "No exception message was provided."
                : ex.Message.Trim();

            var innerMessage = ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message)
                ? $"\nInner: {ex.InnerException.Message.Trim()}"
                : string.Empty;

            return $"{message}{innerMessage}\nType: {ex.GetType().Name}\nHResult: 0x{ex.HResult:X8}";
        }

    }
}
