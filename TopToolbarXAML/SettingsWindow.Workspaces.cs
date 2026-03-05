// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Providers;
using TopToolbar.Services.Workspaces;
using TopToolbar.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TopToolbar
{
    public sealed partial class SettingsWindow
    {
        private sealed record TemplateChoice(string Name, string DisplayName, string Description, int WindowCount);
        private sealed class TemplateEditorState
        {
            public string Name { get; set; } = string.Empty;

            public string DisplayName { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;

            public string LayoutStrategy { get; set; } = "side-by-side";

            public string MonitorPolicy { get; set; } = "primary";

            public bool RequiresRepo { get; set; } = true;

            public string DefaultRepoRoot { get; set; } = string.Empty;

            public bool CreateWorktreeByDefault { get; set; }

            public string WorktreeBaseBranch { get; set; } = "main";

            public bool EnableAgent { get; set; }

            public string AgentName { get; set; } = "copilot";

            public string AgentCommand { get; set; } = string.Empty;

            public bool IncludeTerminal { get; set; } = true;

            public bool IncludeVsCode { get; set; } = true;

            public bool IncludeVisualStudio { get; set; }

            public string VisualStudioSolutionPath { get; set; } = string.Empty;

            public bool IncludeLogsFolder { get; set; }

            public string LogsPath { get; set; } = string.Empty;
        }

        private TemplateDefinition _selectedTemplateDefinition;
        private List<TemplateWindowDefinition> _templateCustomWindows = new();
        private bool _isTemplateEditorLoading;
        private bool _templateEditorEventsBound;

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
                var templates = (await orchestrator.ListTemplatesAsync(CancellationToken.None).ConfigureAwait(true))
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

                var dialogContent = new StackPanel { Spacing = 12 };
                dialogContent.Children.Add(new TextBlock { Text = "Template" });
                dialogContent.Children.Add(templatePicker);

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "Launch window set from template",
                    PrimaryButtonText = "Launch",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = dialogContent,
                    IsPrimaryButtonEnabled = false,
                };

                void UpdateCreateEnabled()
                {
                    var selected = templatePicker.SelectedItem as TemplateChoice;
                    dialog.IsPrimaryButtonEnabled = selected != null;
                }

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
                        NoLaunch = false,
                        EphemeralLaunch = true,
                    },
                    CancellationToken.None).ConfigureAwait(true);

                if (!createResult.Success)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Launch failed", createResult.Message);
                    return;
                }

                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Window set launched", createResult.Message);
            }
            catch (Exception ex)
            {
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Launch failed", ex.Message);
            }
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
            var hintBlock = new TextBlock
            {
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "Create an agent workspace template.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(templateNameBox);
            content.Children.Add(displayNameBox);
            content.Children.Add(descriptionBox);
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
                    requiresRepo: true);

                await orchestrator.SaveTemplateAsync(template, CancellationToken.None).ConfigureAwait(true);
                await RefreshTemplateListForPageAsync().ConfigureAwait(true);
                await SelectTemplateForEditingAsync(normalizedTemplateName).ConfigureAwait(true);
                await ShowSimpleMessageOnUiThreadAsync(
                    xamlRoot,
                    "Template created",
                    $"Template '{normalizedTemplateName}' was created.");
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

        private async void OnSaveSelectedTemplate(object sender, RoutedEventArgs e)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                return;
            }

            if (!queue.HasThreadAccess)
            {
                await RunOnUiThreadAsync(() =>
                {
                    OnSaveSelectedTemplate(sender, e);
                    return Task.CompletedTask;
                }, queue).ConfigureAwait(true);
                return;
            }

            if (_disposed || _isClosed || Content is not FrameworkElement root)
            {
                return;
            }

            if (_selectedTemplateDefinition == null)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            try
            {
                var state = BuildTemplateEditorStateFromUi();
                if (state == null || string.IsNullOrWhiteSpace(state.Name))
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Save failed", "No template is selected.");
                    return;
                }

                var template = BuildTemplateDefinitionFromEditorState(state, _templateCustomWindows);
                TemplateDefinitionStandardizer.StandardizeInPlace(template);
                var validationErrors = TemplateDefinitionValidator.Validate(template);
                if (validationErrors.Count > 0)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        if (TemplateEditorStatusText != null)
                        {
                            TemplateEditorStatusText.Text = string.Join(Environment.NewLine, validationErrors);
                        }

                        return Task.CompletedTask;
                    }, queue).ConfigureAwait(true);

                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Validation failed", string.Join(Environment.NewLine, validationErrors));
                    return;
                }

                using var orchestrator = new WorkspaceTemplateOrchestrator();
                await orchestrator.SaveTemplateAsync(template, CancellationToken.None).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    _selectedTemplateDefinition = template;
                    if (TemplateEditorStatusText != null)
                    {
                        TemplateEditorStatusText.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
                    }

                    return Task.CompletedTask;
                }, queue).ConfigureAwait(true);

                await RefreshTemplateListForPageAsync().ConfigureAwait(true);
                await SelectTemplateForEditingAsync(template.Name).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: save template failed.", ex);
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Save failed", BuildExceptionDisplayMessage(ex));
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

        private Task EnsureUiThreadAsync(DispatcherQueue dispatcherQueue = null)
        {
            var queue = dispatcherQueue ?? DispatcherQueue;

            if (queue == null || queue.HasThreadAccess)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(() => tcs.TrySetResult(true)))
            {
                tcs.TrySetResult(true);
            }

            return tcs.Task;
        }

        private Task RunOnUiThreadAsync(Func<Task> action, DispatcherQueue dispatcherQueue = null)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            var queue = dispatcherQueue ?? DispatcherQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(async () =>
            {
                var previous = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                    await action().ConfigureAwait(true);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue UI work."));
            }

            return tcs.Task;
        }

        private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action, DispatcherQueue dispatcherQueue = null)
        {
            if (action == null)
            {
                return Task.FromResult(default(T));
            }

            var queue = dispatcherQueue ?? DispatcherQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(async () =>
            {
                var previous = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                    var result = await action().ConfigureAwait(true);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue UI work."));
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
            var state = new TemplateEditorState
            {
                Name = templateName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? templateName : displayName,
                Description = description ?? string.Empty,
                RequiresRepo = requiresRepo,
                DefaultRepoRoot = string.Empty,
                LayoutStrategy = "side-by-side",
                MonitorPolicy = "primary",
                EnableAgent = true,
                AgentName = "copilot",
                AgentCommand = "copilot",
                IncludeTerminal = true,
                IncludeVsCode = true,
                IncludeVisualStudio = false,
                IncludeLogsFolder = false,
                LogsPath = "{repo}",
                CreateWorktreeByDefault = false,
                WorktreeBaseBranch = "main",
            };

            return BuildTemplateDefinitionFromEditorState(state, Array.Empty<TemplateWindowDefinition>());
        }

        private async Task RefreshTemplateListForPageAsync()
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                return;
            }

            if (!queue.HasThreadAccess)
            {
                await RunOnUiThreadAsync(() => RefreshTemplateListForPageAsync(), queue).ConfigureAwait(true);
                return;
            }

            if (_disposed || _isClosed || Content is not FrameworkElement root || TemplatesList == null)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            var dispatcher = root.DispatcherQueue ?? DispatcherQueue;
            EnsureTemplateEditorEventBindings();
            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var templates = await orchestrator.ListTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
                var choices = templates
                    .Where(template => template != null)
                    .Select(template => new TemplateChoice(
                        template.Name,
                        string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName,
                        template.Description ?? string.Empty,
                        template.Windows?.Count ?? 0))
                    .OrderBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await RunOnUiThreadAsync(() =>
                {
                    var previousSelection = TemplatesList.SelectedItem as TemplateChoice;
                    TemplatesList.ItemsSource = choices;
                    TemplatesList.SelectedItem = choices.FirstOrDefault(choice =>
                        previousSelection != null &&
                        string.Equals(choice.Name, previousSelection.Name, StringComparison.OrdinalIgnoreCase))
                        ?? choices.FirstOrDefault();
                    UpdateTemplatePageSelectionState();
                    return Task.CompletedTask;
                }, dispatcher).ConfigureAwait(true);

                await LoadSelectedTemplateIntoEditorAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: failed to refresh template list.", ex);
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template list failed", BuildExceptionDisplayMessage(ex));
            }
        }

        private TemplateChoice GetSelectedTemplateChoice()
        {
            return TemplatesList?.SelectedItem as TemplateChoice;
        }

        private void UpdateTemplatePageSelectionState()
        {
            var hasSelection = GetSelectedTemplateChoice() != null;
            if (DeleteSelectedTemplateButton != null)
            {
                DeleteSelectedTemplateButton.IsEnabled = hasSelection;
            }

            if (SaveSelectedTemplateButton != null)
            {
                SaveSelectedTemplateButton.IsEnabled = hasSelection;
            }

            if (TemplatesEmptyText != null)
            {
                var hasItems = TemplatesList?.Items?.Count > 0;
                TemplatesEmptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            }

            if (TemplateEditorHintText != null && !hasSelection)
            {
                TemplateEditorHintText.Text = "Select a template from the left list.";
            }
        }

        private async void OnTemplatesListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                UpdateTemplatePageSelectionState();
                await LoadSelectedTemplateIntoEditorAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: template selection changed handler failed.", ex);
            }
        }

        private async Task SelectTemplateForEditingAsync(string templateName)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                return;
            }

            if (!queue.HasThreadAccess)
            {
                await RunOnUiThreadAsync(() => SelectTemplateForEditingAsync(templateName), queue).ConfigureAwait(true);
                return;
            }

            if (string.IsNullOrWhiteSpace(templateName) || TemplatesList == null)
            {
                return;
            }

            var choice = (TemplatesList.ItemsSource as IEnumerable<TemplateChoice>)
                ?.FirstOrDefault(item => string.Equals(item.Name, templateName, StringComparison.OrdinalIgnoreCase));
            if (choice == null)
            {
                return;
            }

            TemplatesList.SelectedItem = choice;
            await LoadSelectedTemplateIntoEditorAsync().ConfigureAwait(true);
        }

        private async Task LoadSelectedTemplateIntoEditorAsync()
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                return;
            }

            if (!queue.HasThreadAccess)
            {
                await RunOnUiThreadAsync(() => LoadSelectedTemplateIntoEditorAsync(), queue).ConfigureAwait(true);
                return;
            }

            if (_disposed || _isClosed || _isTemplateEditorLoading || Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;

            var selected = GetSelectedTemplateChoice();
            if (selected == null)
            {
                _selectedTemplateDefinition = null;
                _templateCustomWindows = new List<TemplateWindowDefinition>();
                ApplyTemplateEditorStateToUi(new TemplateEditorState
                {
                    Name = string.Empty,
                    DisplayName = string.Empty,
                    Description = string.Empty,
                    LayoutStrategy = "side-by-side",
                    MonitorPolicy = "primary",
                    RequiresRepo = true,
                    DefaultRepoRoot = string.Empty,
                    WorktreeBaseBranch = "main",
                    AgentName = "copilot",
                    LogsPath = "{repo}",
                });
                return;
            }

            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var template = await orchestrator.GetTemplateAsync(selected.Name, CancellationToken.None).ConfigureAwait(true);
                if (template == null)
                {
                    await RefreshTemplateListForPageAsync().ConfigureAwait(true);
                    return;
                }

                _selectedTemplateDefinition = template;
                var state = BuildTemplateEditorStateFromTemplate(template, out var customWindows);
                _templateCustomWindows = customWindows;
                ApplyTemplateEditorStateToUi(state);
                if (TemplateEditorHintText != null)
                {
                    TemplateEditorHintText.Text = $"Editing template '{template.Name}'.";
                }

                if (TemplateEditorStatusText != null)
                {
                    TemplateEditorStatusText.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: load template into editor failed.", ex);
                await RunOnUiThreadAsync(() =>
                {
                    if (TemplateEditorStatusText != null)
                    {
                        TemplateEditorStatusText.Text = $"Template load failed: {BuildExceptionDisplayMessage(ex)}";
                    }

                    return Task.CompletedTask;
                }, queue).ConfigureAwait(true);
            }
        }

        private void ApplyTemplateEditorStateToUi(TemplateEditorState state)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                return;
            }

            if (!queue.HasThreadAccess)
            {
                _ = queue.TryEnqueue(() => ApplyTemplateEditorStateToUi(state));
                return;
            }

            if (state == null)
            {
                return;
            }

            _isTemplateEditorLoading = true;
            try
            {
                if (TemplateIdTextBox != null)
                {
                    TemplateIdTextBox.Text = state.Name;
                }

                if (TemplateDisplayNameTextBox != null)
                {
                    TemplateDisplayNameTextBox.Text = state.DisplayName;
                }

                if (TemplateDescriptionTextBox != null)
                {
                    TemplateDescriptionTextBox.Text = state.Description;
                }

                var effectiveLayoutStrategy = GetLayoutStrategyByWindowCount(GetConfiguredWindowCount(state));
                SelectComboItemByContent(TemplateLayoutStrategyComboBox, effectiveLayoutStrategy);
                SelectComboItemByContent(TemplateMonitorPolicyComboBox, state.MonitorPolicy);
                SelectComboItemByContent(TemplateAgentComboBox, state.AgentName);

                if (TemplateRequiresRepoToggle != null)
                {
                    TemplateRequiresRepoToggle.IsOn = state.RequiresRepo;
                }

                if (TemplateDefaultRepoTextBox != null)
                {
                    TemplateDefaultRepoTextBox.Text = state.DefaultRepoRoot;
                }

                if (TemplateCreateWorktreeToggle != null)
                {
                    TemplateCreateWorktreeToggle.IsOn = state.CreateWorktreeByDefault;
                }

                if (TemplateWorktreeBaseBranchTextBox != null)
                {
                    TemplateWorktreeBaseBranchTextBox.Text = string.IsNullOrWhiteSpace(state.WorktreeBaseBranch) ? "main" : state.WorktreeBaseBranch;
                }

                if (TemplateEnableAgentToggle != null)
                {
                    TemplateEnableAgentToggle.IsOn = state.EnableAgent;
                }

                if (TemplateAgentCommandTextBox != null)
                {
                    TemplateAgentCommandTextBox.Text = state.AgentCommand;
                }

                if (TemplateIncludeTerminalToggle != null)
                {
                    TemplateIncludeTerminalToggle.IsOn = state.IncludeTerminal || state.EnableAgent;
                }

                if (TemplateIncludeVsCodeToggle != null)
                {
                    TemplateIncludeVsCodeToggle.IsOn = state.IncludeVsCode;
                }

                if (TemplateIncludeVisualStudioToggle != null)
                {
                    TemplateIncludeVisualStudioToggle.IsOn = state.IncludeVisualStudio;
                }

                if (TemplateVisualStudioSolutionTextBox != null)
                {
                    TemplateVisualStudioSolutionTextBox.Text = state.VisualStudioSolutionPath;
                }

                if (TemplateIncludeLogsToggle != null)
                {
                    TemplateIncludeLogsToggle.IsOn = state.IncludeLogsFolder;
                }

                if (TemplateLogsPathTextBox != null)
                {
                    TemplateLogsPathTextBox.Text = state.LogsPath;
                }
            }
            finally
            {
                _isTemplateEditorLoading = false;
            }

            UpdateTemplateLayoutPreviewFromUi();
        }

        private void EnsureTemplateEditorEventBindings()
        {
            if (_templateEditorEventsBound)
            {
                return;
            }

            if (TemplateEnableAgentToggle == null
                || TemplateIncludeTerminalToggle == null
                || TemplateIncludeVsCodeToggle == null
                || TemplateIncludeVisualStudioToggle == null
                || TemplateIncludeLogsToggle == null)
            {
                return;
            }

            TemplateEnableAgentToggle.Toggled += OnTemplateEditorToggleChanged;
            TemplateIncludeTerminalToggle.Toggled += OnTemplateEditorToggleChanged;
            TemplateIncludeVsCodeToggle.Toggled += OnTemplateEditorToggleChanged;
            TemplateIncludeVisualStudioToggle.Toggled += OnTemplateEditorToggleChanged;
            TemplateIncludeLogsToggle.Toggled += OnTemplateEditorToggleChanged;

            _templateEditorEventsBound = true;
        }

        private void OnTemplateEditorToggleChanged(object sender, RoutedEventArgs e)
        {
            UpdateTemplateLayoutPreviewFromUi();
        }

        private void UpdateTemplateLayoutPreviewFromUi()
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                return;
            }

            if (!queue.HasThreadAccess)
            {
                _ = queue.TryEnqueue(UpdateTemplateLayoutPreviewFromUi);
                return;
            }

            if (_isTemplateEditorLoading)
            {
                return;
            }

            var state = BuildTemplateEditorStateFromUi();
            if (state == null)
            {
                return;
            }

            _isTemplateEditorLoading = true;
            try
            {
                if (TemplateIncludeTerminalToggle != null)
                {
                    if (state.EnableAgent && !TemplateIncludeTerminalToggle.IsOn)
                    {
                        TemplateIncludeTerminalToggle.IsOn = true;
                    }

                    TemplateIncludeTerminalToggle.IsEnabled = !state.EnableAgent;
                }

                var effectiveState = BuildTemplateEditorStateFromUi();
                if (effectiveState == null)
                {
                    return;
                }

                var count = GetConfiguredWindowCount(effectiveState);
                var strategy = GetLayoutStrategyByWindowCount(count);
                SelectComboItemByContent(TemplateLayoutStrategyComboBox, strategy);
            }
            finally
            {
                _isTemplateEditorLoading = false;
            }
        }

        private TemplateEditorState BuildTemplateEditorStateFromUi()
        {
            if (_selectedTemplateDefinition == null)
            {
                return null;
            }

            return new TemplateEditorState
            {
                Name = _selectedTemplateDefinition.Name,
                DisplayName = (TemplateDisplayNameTextBox?.Text ?? string.Empty).Trim(),
                Description = (TemplateDescriptionTextBox?.Text ?? string.Empty).Trim(),
                LayoutStrategy = GetComboBoxSelection(TemplateLayoutStrategyComboBox, "side-by-side"),
                MonitorPolicy = GetComboBoxSelection(TemplateMonitorPolicyComboBox, "primary"),
                RequiresRepo = TemplateRequiresRepoToggle?.IsOn ?? false,
                DefaultRepoRoot = (TemplateDefaultRepoTextBox?.Text ?? string.Empty).Trim(),
                CreateWorktreeByDefault = TemplateCreateWorktreeToggle?.IsOn ?? false,
                WorktreeBaseBranch = string.IsNullOrWhiteSpace(TemplateWorktreeBaseBranchTextBox?.Text)
                    ? "main"
                    : TemplateWorktreeBaseBranchTextBox.Text.Trim(),
                EnableAgent = TemplateEnableAgentToggle?.IsOn ?? false,
                AgentName = GetComboBoxSelection(TemplateAgentComboBox, "copilot"),
                AgentCommand = (TemplateAgentCommandTextBox?.Text ?? string.Empty).Trim(),
                IncludeTerminal = (TemplateIncludeTerminalToggle?.IsOn ?? false) || (TemplateEnableAgentToggle?.IsOn ?? false),
                IncludeVsCode = TemplateIncludeVsCodeToggle?.IsOn ?? false,
                IncludeVisualStudio = TemplateIncludeVisualStudioToggle?.IsOn ?? false,
                VisualStudioSolutionPath = (TemplateVisualStudioSolutionTextBox?.Text ?? string.Empty).Trim(),
                IncludeLogsFolder = TemplateIncludeLogsToggle?.IsOn ?? false,
                LogsPath = (TemplateLogsPathTextBox?.Text ?? string.Empty).Trim(),
            };
        }

        private static TemplateEditorState BuildTemplateEditorStateFromTemplate(
            TemplateDefinition template,
            out List<TemplateWindowDefinition> customWindows)
        {
            customWindows = new List<TemplateWindowDefinition>();
            var state = new TemplateEditorState
            {
                Name = template?.Name ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(template?.DisplayName) ? template?.Name ?? string.Empty : template.DisplayName,
                Description = template?.Description ?? string.Empty,
                LayoutStrategy = string.IsNullOrWhiteSpace(template?.Layout?.Strategy) ? "side-by-side" : template.Layout.Strategy,
                MonitorPolicy = string.IsNullOrWhiteSpace(template?.Layout?.MonitorPolicy) ? "primary" : template.Layout.MonitorPolicy,
                RequiresRepo = template?.RequiresRepo ?? true,
                DefaultRepoRoot = template?.DefaultRepoRoot ?? string.Empty,
                CreateWorktreeByDefault = template?.Creation?.CreateWorktreeByDefault ?? false,
                WorktreeBaseBranch = string.IsNullOrWhiteSpace(template?.Creation?.WorktreeBaseBranch)
                    ? "main"
                    : template.Creation.WorktreeBaseBranch,
                EnableAgent = template?.Agent?.Enabled ?? false,
                AgentName = string.IsNullOrWhiteSpace(template?.Agent?.Name) ? "copilot" : template.Agent.Name,
                AgentCommand = template?.Agent?.Command ?? string.Empty,
                IncludeTerminal = false,
                IncludeVsCode = false,
                IncludeVisualStudio = false,
                IncludeLogsFolder = false,
                LogsPath = "{repo}",
            };

            foreach (var window in template?.Windows ?? new List<TemplateWindowDefinition>())
            {
                if (window == null)
                {
                    continue;
                }

                if (IsExecutable(window.Exe, "wt.exe") && string.Equals(window.Role, "terminal", StringComparison.OrdinalIgnoreCase))
                {
                    state.IncludeTerminal = true;
                    if (state.EnableAgent && string.IsNullOrWhiteSpace(state.AgentCommand))
                    {
                        state.AgentCommand = ExtractAgentCommand(window);
                    }

                    continue;
                }

                if (IsExecutable(window.Exe, "code.exe"))
                {
                    state.IncludeVsCode = true;
                    continue;
                }

                if (IsExecutable(window.Exe, "devenv.exe"))
                {
                    state.IncludeVisualStudio = true;
                    var solutionPath = Dequote(window.Args);
                    if (solutionPath.StartsWith("{repo}\\", StringComparison.OrdinalIgnoreCase))
                    {
                        solutionPath = solutionPath.Substring("{repo}\\".Length);
                    }

                    if (!string.IsNullOrWhiteSpace(solutionPath) && !string.Equals(solutionPath, "{repo}", StringComparison.OrdinalIgnoreCase))
                    {
                        state.VisualStudioSolutionPath = solutionPath;
                    }

                    continue;
                }

                if (IsExecutable(window.Exe, "explorer.exe"))
                {
                    state.IncludeLogsFolder = true;
                    var logsPath = Dequote(window.Args);
                    if (!string.IsNullOrWhiteSpace(logsPath))
                    {
                        state.LogsPath = logsPath;
                    }

                    continue;
                }

                if (string.Equals(window.Role, "agent", StringComparison.OrdinalIgnoreCase)
                    || IsExecutable(window.Exe, "pwsh.exe")
                    || IsExecutable(window.Exe, "powershell.exe")
                    || IsExecutable(window.Exe, "cmd.exe"))
                {
                    state.EnableAgent = true;
                    if (string.IsNullOrWhiteSpace(state.AgentCommand))
                    {
                        state.AgentCommand = ExtractAgentCommand(window);
                    }

                    continue;
                }

                customWindows.Add(CloneWindow(window));
            }

            if (string.IsNullOrWhiteSpace(state.AgentCommand) && state.EnableAgent)
            {
                state.AgentCommand = "copilot";
            }

            if (string.IsNullOrWhiteSpace(state.LogsPath))
            {
                state.LogsPath = "{repo}";
            }

            return state;
        }

        private static TemplateDefinition BuildTemplateDefinitionFromEditorState(
            TemplateEditorState state,
            IReadOnlyList<TemplateWindowDefinition> customWindows)
        {
            _ = customWindows;
            var windows = new List<TemplateWindowDefinition>();
            var includeTerminal = state.IncludeTerminal || state.EnableAgent;
            var agentCommand = NormalizeAgentCommand(
                string.IsNullOrWhiteSpace(state.AgentCommand) ? state.AgentName : state.AgentCommand,
                state.AgentName);

            if (state.IncludeVsCode)
            {
                windows.Add(new TemplateWindowDefinition
                {
                    Role = "editor",
                    Exe = "code.exe",
                    WorkingDirectory = "{repo}",
                    Args = Quote("{repo}"),
                    Monitor = "primary",
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = "code",
                    },
                });
            }

            if (state.IncludeVisualStudio)
            {
                var solutionArg = string.IsNullOrWhiteSpace(state.VisualStudioSolutionPath)
                    ? "{repo}"
                    : state.VisualStudioSolutionPath.StartsWith("{repo}", StringComparison.OrdinalIgnoreCase)
                        ? state.VisualStudioSolutionPath
                        : $@"{{repo}}\{state.VisualStudioSolutionPath}";
                windows.Add(new TemplateWindowDefinition
                {
                    Role = "ide",
                    Exe = "devenv.exe",
                    WorkingDirectory = "{repo}",
                    Args = Quote(solutionArg),
                    Monitor = "primary",
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = "devenv",
                    },
                });
            }

            if (includeTerminal)
            {
                windows.Add(new TemplateWindowDefinition
                {
                    Role = "terminal",
                    Exe = "wt.exe",
                    WorkingDirectory = "{repo}",
                    Args = state.EnableAgent ? $"pwsh -NoExit -Command {Quote(agentCommand)}" : string.Empty,
                    Monitor = "primary",
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = "wt",
                    },
                });
            }

            if (state.IncludeLogsFolder)
            {
                var logsPath = string.IsNullOrWhiteSpace(state.LogsPath) ? "{repo}" : state.LogsPath;
                windows.Add(new TemplateWindowDefinition
                {
                    Role = "logs",
                    Exe = "explorer.exe",
                    Args = Quote(logsPath),
                    Monitor = "primary",
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = "explorer",
                    },
                });
            }

            if (windows.Count == 0)
            {
                windows.Add(new TemplateWindowDefinition
                {
                    Role = "terminal",
                    Exe = "wt.exe",
                    WorkingDirectory = "{repo}",
                    Monitor = "primary",
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = "wt",
                    },
                });
            }

            var roleOrder = new List<string> { "terminal", "editor", "ide", "logs" };
            var allRoles = windows
                .Select(window => (window?.Role ?? string.Empty).Trim().ToLowerInvariant())
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var focusPriority = roleOrder
                .Where(role => allRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var role in allRoles)
            {
                if (!focusPriority.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    focusPriority.Add(role);
                }
            }

            var strategy = GetLayoutStrategyByWindowCount(allRoles.Count);
            var monitorPolicy = string.IsNullOrWhiteSpace(state.MonitorPolicy) ? "primary" : state.MonitorPolicy.Trim();

            var requiresRepo = state.RequiresRepo
                || state.CreateWorktreeByDefault
                || windows.Any(window =>
                    ContainsRepoToken(window?.Exe)
                    || ContainsRepoToken(window?.WorkingDirectory)
                    || ContainsRepoToken(window?.Args)
                    || ContainsRepoToken(window?.Init));

            return new TemplateDefinition
            {
                SchemaVersion = 1,
                Name = state.Name,
                DisplayName = string.IsNullOrWhiteSpace(state.DisplayName) ? state.Name : state.DisplayName,
                Description = state.Description ?? string.Empty,
                RequiresRepo = requiresRepo,
                DefaultRepoRoot = state.DefaultRepoRoot ?? string.Empty,
                FocusPriority = focusPriority,
                Layout = new TemplateLayoutDefinition
                {
                    Strategy = strategy,
                    MonitorPolicy = monitorPolicy,
                    Preset = strategy,
                    Slots = WorkspaceLayoutEngine.BuildSlots(strategy, allRoles),
                },
                Windows = windows,
                Agent = new TemplateAgentDefinition
                {
                    Enabled = state.EnableAgent,
                    Name = string.IsNullOrWhiteSpace(state.AgentName) ? "copilot" : state.AgentName,
                    Command = agentCommand,
                    WorkingDirectory = "{repo}",
                },
                Creation = new TemplateCreationDefinition
                {
                    CreateWorktreeByDefault = state.CreateWorktreeByDefault,
                    WorktreeBaseBranch = string.IsNullOrWhiteSpace(state.WorktreeBaseBranch) ? "main" : state.WorktreeBaseBranch,
                },
            };
        }

        private static string GetLayoutStrategyByWindowCount(int windowCount)
        {
            if (windowCount <= 1)
            {
                return "single";
            }

            if (windowCount == 2)
            {
                return "side-by-side";
            }

            if (windowCount == 3)
            {
                return "triple-columns";
            }

            return "grid-2x2";
        }

        private int GetConfiguredWindowCount(TemplateEditorState state)
        {
            if (state == null)
            {
                return 1;
            }

            var count = 0;
            var includeTerminal = state.IncludeTerminal || state.EnableAgent;

            if (state.IncludeVsCode)
            {
                count++;
            }

            if (state.IncludeVisualStudio)
            {
                count++;
            }

            if (includeTerminal)
            {
                count++;
            }

            if (state.IncludeLogsFolder)
            {
                count++;
            }
            return Math.Max(count, 1);
        }

        private static TemplateWindowDefinition CloneWindow(TemplateWindowDefinition window)
        {
            if (window == null)
            {
                return new TemplateWindowDefinition();
            }

            return new TemplateWindowDefinition
            {
                Role = window.Role ?? string.Empty,
                Exe = window.Exe ?? string.Empty,
                WorkingDirectory = window.WorkingDirectory ?? string.Empty,
                Args = window.Args ?? string.Empty,
                Init = window.Init ?? string.Empty,
                Monitor = window.Monitor ?? string.Empty,
                MatchHints = new TemplateMatchHints
                {
                    ProcessName = window.MatchHints?.ProcessName ?? string.Empty,
                    ProcessPath = window.MatchHints?.ProcessPath ?? string.Empty,
                    Title = window.MatchHints?.Title ?? string.Empty,
                    AppUserModelId = window.MatchHints?.AppUserModelId ?? string.Empty,
                },
            };
        }

        private static bool IsExecutable(string executablePath, string expectedExe)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(expectedExe))
            {
                return false;
            }

            var actual = Path.GetFileName(executablePath.Trim().Trim('"'));
            return string.Equals(actual, expectedExe, StringComparison.OrdinalIgnoreCase);
        }

        private static void SelectComboItemByContent(ComboBox comboBox, string value)
        {
            if (comboBox == null)
            {
                return;
            }

            var target = (value ?? string.Empty).Trim();
            foreach (var item in comboBox.Items)
            {
                if (item is ComboBoxItem comboBoxItem)
                {
                    var content = comboBoxItem.Content?.ToString() ?? string.Empty;
                    if (string.Equals(content, target, StringComparison.OrdinalIgnoreCase))
                    {
                        comboBox.SelectedItem = comboBoxItem;
                        return;
                    }
                }
                else if (item != null && string.Equals(item.ToString(), target, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            if (comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        private static string GetComboBoxSelection(ComboBox comboBox, string fallback)
        {
            if (comboBox?.SelectedItem is ComboBoxItem selectedComboBoxItem)
            {
                var value = selectedComboBoxItem.Content?.ToString();
                return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            }

            if (comboBox?.SelectedItem != null)
            {
                var value = comboBox.SelectedItem.ToString();
                return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(comboBox?.Text))
            {
                return comboBox.Text.Trim();
            }

            return fallback;
        }

        private static string Quote(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var escaped = normalized.Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        }

        private static string Dequote(string value)
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
                    normalized = normalized.Substring(1, normalized.Length - 2).Trim();
                }

                if (normalized.Length >= 2 && normalized[0] == '\'' && normalized[^1] == '\'')
                {
                    normalized = normalized.Substring(1, normalized.Length - 2).Trim();
                }

                if (string.Equals(before, normalized, StringComparison.Ordinal))
                {
                    break;
                }
            }

            return normalized.Trim();
        }

        private static string NormalizeAgentCommand(string command, string fallbackName)
        {
            var normalized = Dequote(command);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = (fallbackName ?? string.Empty).Trim();
            }

            return Dequote(normalized);
        }

        private static string ExtractAgentCommand(TemplateWindowDefinition window)
        {
            var init = Dequote(window?.Init);
            if (!string.IsNullOrWhiteSpace(init))
            {
                return init;
            }

            var args = (window?.Args ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(args))
            {
                return string.Empty;
            }

            var commandMarker = "-Command";
            var markerIndex = args.IndexOf(commandMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var raw = args.Substring(markerIndex + commandMarker.Length).Trim();
                return Dequote(raw);
            }

            return Dequote(args);
        }

        private static bool ContainsRepoToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf("{repo}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async void OnManageWorkspaceTemplates(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isClosed || Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            var selected = GetSelectedTemplateChoice();
            if (selected == null)
            {
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "No template selected", "Select a template from the list first.");
                return;
            }

            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var template = await orchestrator.GetTemplateAsync(selected.Name, CancellationToken.None).ConfigureAwait(true);
                if (template == null)
                {
                    await RefreshTemplateListForPageAsync().ConfigureAwait(true);
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template missing", $"Template '{selected.Name}' no longer exists.");
                    return;
                }

                var saved = await ShowTemplateDesignerAsync(xamlRoot, template, orchestrator).ConfigureAwait(true);
                if (saved)
                {
                    await RefreshTemplateListForPageAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: template management failed.", ex);
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template management failed", BuildExceptionDisplayMessage(ex));
            }
        }

        private async void OnDeleteSelectedTemplate(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isClosed || Content is not FrameworkElement root)
            {
                return;
            }

            var xamlRoot = root.XamlRoot;
            var selected = GetSelectedTemplateChoice();
            if (selected == null)
            {
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "No template selected", "Select a template from the list first.");
                return;
            }

            try
            {
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

                using var orchestrator = new WorkspaceTemplateOrchestrator();
                var deleted = await orchestrator.DeleteTemplateAsync(selected.Name, CancellationToken.None).ConfigureAwait(true);
                if (!deleted)
                {
                    await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Delete failed", $"Template '{selected.Name}' was not deleted.");
                    return;
                }

                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Template deleted", $"Template '{selected.Name}' was deleted.");
                await RefreshTemplateListForPageAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("SettingsWindow: delete template failed.", ex);
                await ShowSimpleMessageOnUiThreadAsync(xamlRoot, "Delete failed", BuildExceptionDisplayMessage(ex));
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
