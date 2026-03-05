// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TopToolbar
{
    public sealed partial class SettingsWindow
    {
        private async Task<bool> ShowTemplateDesignerAsync(
            XamlRoot xamlRoot,
            TopToolbar.Services.Workspaces.TemplateDefinition template,
            TopToolbar.Services.Workspaces.WorkspaceTemplateOrchestrator orchestrator)
        {
            if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
            {
                return await RunOnUiThreadAsync(
                    () => ShowTemplateDesignerAsync(xamlRoot, template, orchestrator),
                    DispatcherQueue).ConfigureAwait(true);
            }

            if (xamlRoot == null || template == null || orchestrator == null)
            {
                return false;
            }

            var working = CloneTemplateDefinition(template);
            working.Layout ??= new TopToolbar.Services.Workspaces.TemplateLayoutDefinition();
            working.Windows ??= new List<TopToolbar.Services.Workspaces.TemplateWindowDefinition>();
            working.Layout.Slots ??= new List<TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition>();
            working.FocusPriority ??= new List<string>();

            var appRows = new ObservableCollection<TopToolbar.Services.Workspaces.TemplateWindowDefinition>(
                working.Windows.Select(CloneTemplateWindowDefinition));
            var slotRows = new ObservableCollection<TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition>(
                working.Layout.Slots.Select(CloneTemplateLayoutSlotDefinition));

            var appItems = new ObservableCollection<string>();
            var slotItems = new ObservableCollection<string>();

            var nameBox = new TextBox
            {
                IsReadOnly = true,
                Text = working.Name ?? string.Empty,
                MinWidth = 320,
            };
            var displayNameBox = new TextBox
            {
                Text = working.DisplayName ?? string.Empty,
                MinWidth = 320,
            };
            var descriptionBox = new TextBox
            {
                Text = working.Description ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 72,
                MinWidth = 320,
            };
            var requiresRepoToggle = new ToggleSwitch
            {
                IsOn = working.RequiresRepo,
            };
            var defaultRepoBox = new TextBox
            {
                Text = working.DefaultRepoRoot ?? string.Empty,
                MinWidth = 320,
            };
            var strategyPicker = new ComboBox
            {
                ItemsSource = new[] { "single", "side-by-side", "triple-columns", "grid-2x2" },
                SelectedItem = string.IsNullOrWhiteSpace(working.Layout.Strategy) ? "single" : working.Layout.Strategy,
                MinWidth = 240,
            };
            var monitorPolicyOptions = new List<string> { "primary", "any", "current" };
            var currentMonitorPolicy = string.IsNullOrWhiteSpace(working.Layout.MonitorPolicy) ? "primary" : working.Layout.MonitorPolicy;
            if (!monitorPolicyOptions.Contains(currentMonitorPolicy, StringComparer.OrdinalIgnoreCase))
            {
                monitorPolicyOptions.Add(currentMonitorPolicy);
            }

            var monitorPolicyPicker = new ComboBox
            {
                ItemsSource = monitorPolicyOptions,
                SelectedItem = monitorPolicyOptions.FirstOrDefault(option =>
                    string.Equals(option, currentMonitorPolicy, StringComparison.OrdinalIgnoreCase)) ?? "primary",
                MinWidth = 240,
            };
            var focusPriorityBox = new TextBox
            {
                Text = string.Join(", ", working.FocusPriority ?? new List<string>()),
                MinWidth = 320,
            };

            var appsList = new ListView
            {
                MinHeight = 140,
                MaxHeight = 180,
                SelectionMode = ListViewSelectionMode.Single,
                ItemsSource = appItems,
            };
            var addAppButton = new Button { Content = "Add app" };
            var removeAppButton = new Button { Content = "Remove app" };

            var appRoleBox = new TextBox { PlaceholderText = "role", MinWidth = 220 };
            var appExeBox = new TextBox { PlaceholderText = "exe", MinWidth = 220 };
            var appCwdBox = new TextBox { PlaceholderText = "cwd", MinWidth = 220 };
            var appArgsBox = new TextBox { PlaceholderText = "args", MinWidth = 220 };
            var appInitBox = new TextBox { PlaceholderText = "init", MinWidth = 220 };
            var appMonitorBox = new TextBox { PlaceholderText = "monitor", MinWidth = 220 };
            var appMatchProcessNameBox = new TextBox { PlaceholderText = "matchHints.processName", MinWidth = 220 };
            var appMatchProcessPathBox = new TextBox { PlaceholderText = "matchHints.processPath", MinWidth = 220 };
            var appMatchTitleBox = new TextBox { PlaceholderText = "matchHints.title", MinWidth = 220 };
            var appMatchAumidBox = new TextBox { PlaceholderText = "matchHints.appUserModelId", MinWidth = 220 };

            var slotsList = new ListView
            {
                MinHeight = 120,
                MaxHeight = 160,
                SelectionMode = ListViewSelectionMode.Single,
                ItemsSource = slotItems,
            };
            var addSlotButton = new Button { Content = "Add slot" };
            var removeSlotButton = new Button { Content = "Remove slot" };
            var slotRoleBox = new TextBox { PlaceholderText = "role", MinWidth = 160 };
            var slotXBox = new TextBox { PlaceholderText = "x", MinWidth = 100 };
            var slotYBox = new TextBox { PlaceholderText = "y", MinWidth = 100 };
            var slotWBox = new TextBox { PlaceholderText = "w", MinWidth = 100 };
            var slotHBox = new TextBox { PlaceholderText = "h", MinWidth = 100 };

            var validationText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 900,
            };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = "General", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = "Template id" });
            content.Children.Add(nameBox);
            content.Children.Add(new TextBlock { Text = "Display name" });
            content.Children.Add(displayNameBox);
            content.Children.Add(new TextBlock { Text = "Description" });
            content.Children.Add(descriptionBox);
            content.Children.Add(new TextBlock { Text = "Requires repo" });
            content.Children.Add(requiresRepoToggle);
            content.Children.Add(new TextBlock { Text = "Default repo root" });
            content.Children.Add(defaultRepoBox);
            content.Children.Add(new TextBlock { Text = "Layout strategy" });
            content.Children.Add(strategyPicker);
            content.Children.Add(new TextBlock { Text = "Monitor policy" });
            content.Children.Add(monitorPolicyPicker);
            content.Children.Add(new TextBlock { Text = "Focus priority (comma separated roles)" });
            content.Children.Add(focusPriorityBox);

            content.Children.Add(new TextBlock { Text = "Applications", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(appsList);
            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { addAppButton, removeAppButton },
            });
            content.Children.Add(appRoleBox);
            content.Children.Add(appExeBox);
            content.Children.Add(appCwdBox);
            content.Children.Add(appArgsBox);
            content.Children.Add(appInitBox);
            content.Children.Add(appMonitorBox);
            content.Children.Add(appMatchProcessNameBox);
            content.Children.Add(appMatchProcessPathBox);
            content.Children.Add(appMatchTitleBox);
            content.Children.Add(appMatchAumidBox);

            content.Children.Add(new TextBlock { Text = "Layout slots", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(slotsList);
            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { addSlotButton, removeSlotButton },
            });
            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { slotRoleBox, slotXBox, slotYBox, slotWBox, slotHBox },
            });

            content.Children.Add(new TextBlock { Text = "Validation", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(validationText);

            bool suppressAppEdit = false;
            bool suppressSlotEdit = false;

            void RefreshAppItems()
            {
                appItems.Clear();
                for (var i = 0; i < appRows.Count; i++)
                {
                    var app = appRows[i];
                    var role = string.IsNullOrWhiteSpace(app.Role) ? "(role?)" : app.Role;
                    var exe = string.IsNullOrWhiteSpace(app.Exe) ? "(exe?)" : app.Exe;
                    appItems.Add($"{i + 1}. {role} -> {exe}");
                }
            }

            void RefreshSlotItems()
            {
                slotItems.Clear();
                for (var i = 0; i < slotRows.Count; i++)
                {
                    var slot = slotRows[i];
                    var role = string.IsNullOrWhiteSpace(slot.Role) ? "(role?)" : slot.Role;
                    slotItems.Add($"{i + 1}. {role}: x={slot.X:0.##}, y={slot.Y:0.##}, w={slot.Width:0.##}, h={slot.Height:0.##}");
                }
            }

            void LoadSelectedAppDetails()
            {
                suppressAppEdit = true;
                try
                {
                    if (appsList.SelectedIndex < 0 || appsList.SelectedIndex >= appRows.Count)
                    {
                        appRoleBox.Text = string.Empty;
                        appExeBox.Text = string.Empty;
                        appCwdBox.Text = string.Empty;
                        appArgsBox.Text = string.Empty;
                        appInitBox.Text = string.Empty;
                        appMonitorBox.Text = string.Empty;
                        appMatchProcessNameBox.Text = string.Empty;
                        appMatchProcessPathBox.Text = string.Empty;
                        appMatchTitleBox.Text = string.Empty;
                        appMatchAumidBox.Text = string.Empty;
                        return;
                    }

                    var app = appRows[appsList.SelectedIndex];
                    app.MatchHints ??= new TopToolbar.Services.Workspaces.TemplateMatchHints();
                    appRoleBox.Text = app.Role ?? string.Empty;
                    appExeBox.Text = app.Exe ?? string.Empty;
                    appCwdBox.Text = app.WorkingDirectory ?? string.Empty;
                    appArgsBox.Text = app.Args ?? string.Empty;
                    appInitBox.Text = app.Init ?? string.Empty;
                    appMonitorBox.Text = app.Monitor ?? string.Empty;
                    appMatchProcessNameBox.Text = app.MatchHints.ProcessName ?? string.Empty;
                    appMatchProcessPathBox.Text = app.MatchHints.ProcessPath ?? string.Empty;
                    appMatchTitleBox.Text = app.MatchHints.Title ?? string.Empty;
                    appMatchAumidBox.Text = app.MatchHints.AppUserModelId ?? string.Empty;
                }
                finally
                {
                    suppressAppEdit = false;
                }
            }

            void LoadSelectedSlotDetails()
            {
                suppressSlotEdit = true;
                try
                {
                    if (slotsList.SelectedIndex < 0 || slotsList.SelectedIndex >= slotRows.Count)
                    {
                        slotRoleBox.Text = string.Empty;
                        slotXBox.Text = string.Empty;
                        slotYBox.Text = string.Empty;
                        slotWBox.Text = string.Empty;
                        slotHBox.Text = string.Empty;
                        return;
                    }

                    var slot = slotRows[slotsList.SelectedIndex];
                    slotRoleBox.Text = slot.Role ?? string.Empty;
                    slotXBox.Text = slot.X.ToString("0.###", CultureInfo.InvariantCulture);
                    slotYBox.Text = slot.Y.ToString("0.###", CultureInfo.InvariantCulture);
                    slotWBox.Text = slot.Width.ToString("0.###", CultureInfo.InvariantCulture);
                    slotHBox.Text = slot.Height.ToString("0.###", CultureInfo.InvariantCulture);
                }
                finally
                {
                    suppressSlotEdit = false;
                }
            }

            TopToolbar.Services.Workspaces.TemplateDefinition BuildCandidate()
            {
                var candidate = CloneTemplateDefinition(working);
                candidate.DisplayName = displayNameBox.Text?.Trim() ?? string.Empty;
                candidate.Description = descriptionBox.Text?.Trim() ?? string.Empty;
                candidate.RequiresRepo = requiresRepoToggle.IsOn;
                candidate.DefaultRepoRoot = defaultRepoBox.Text?.Trim() ?? string.Empty;
                candidate.Layout ??= new TopToolbar.Services.Workspaces.TemplateLayoutDefinition();
                candidate.Layout.Strategy = strategyPicker.SelectedItem as string ?? strategyPicker.Text ?? "single";
                candidate.Layout.MonitorPolicy = monitorPolicyPicker.SelectedItem as string ?? "primary";
                candidate.Layout.Preset = candidate.Layout.Strategy;
                candidate.Windows = appRows.Select(CloneTemplateWindowDefinition).ToList();
                candidate.Layout.Slots = slotRows.Select(CloneTemplateLayoutSlotDefinition).ToList();
                candidate.FocusPriority = (focusPriorityBox.Text ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(role => role.Trim().ToLowerInvariant())
                    .Where(role => !string.IsNullOrWhiteSpace(role))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return candidate;
            }

            void UpdateValidationState(ContentDialog dialogRef)
            {
                var candidate = BuildCandidate();
                TopToolbar.Services.Workspaces.TemplateDefinitionStandardizer.StandardizeInPlace(candidate);
                var errors = TopToolbar.Services.Workspaces.TemplateDefinitionValidator.Validate(candidate);
                if (errors.Count == 0)
                {
                    validationText.Text = "Template is valid.";
                    validationText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32));
                    dialogRef.IsPrimaryButtonEnabled = true;
                }
                else
                {
                    validationText.Text = string.Join(Environment.NewLine, errors);
                    validationText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC6, 0x28, 0x28));
                    dialogRef.IsPrimaryButtonEnabled = false;
                }
            }

            void UpdateSelectedAppFromFields()
            {
                if (suppressAppEdit || appsList.SelectedIndex < 0 || appsList.SelectedIndex >= appRows.Count)
                {
                    return;
                }

                var app = appRows[appsList.SelectedIndex];
                app.MatchHints ??= new TopToolbar.Services.Workspaces.TemplateMatchHints();
                app.Role = appRoleBox.Text?.Trim() ?? string.Empty;
                app.Exe = appExeBox.Text?.Trim() ?? string.Empty;
                app.WorkingDirectory = appCwdBox.Text?.Trim() ?? string.Empty;
                app.Args = appArgsBox.Text?.Trim() ?? string.Empty;
                app.Init = appInitBox.Text?.Trim() ?? string.Empty;
                app.Monitor = appMonitorBox.Text?.Trim() ?? string.Empty;
                app.MatchHints.ProcessName = appMatchProcessNameBox.Text?.Trim() ?? string.Empty;
                app.MatchHints.ProcessPath = appMatchProcessPathBox.Text?.Trim() ?? string.Empty;
                app.MatchHints.Title = appMatchTitleBox.Text?.Trim() ?? string.Empty;
                app.MatchHints.AppUserModelId = appMatchAumidBox.Text?.Trim() ?? string.Empty;
                RefreshAppItems();
            }

            void UpdateSelectedSlotFromFields()
            {
                if (suppressSlotEdit || slotsList.SelectedIndex < 0 || slotsList.SelectedIndex >= slotRows.Count)
                {
                    return;
                }

                var slot = slotRows[slotsList.SelectedIndex];
                slot.Role = slotRoleBox.Text?.Trim() ?? string.Empty;
                if (double.TryParse(slotXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                {
                    slot.X = x;
                }

                if (double.TryParse(slotYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    slot.Y = y;
                }

                if (double.TryParse(slotWBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                {
                    slot.Width = w;
                }

                if (double.TryParse(slotHBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                {
                    slot.Height = h;
                }

                RefreshSlotItems();
            }

            appsList.SelectionChanged += (_, __) => LoadSelectedAppDetails();
            slotsList.SelectionChanged += (_, __) => LoadSelectedSlotDetails();

            appRoleBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appExeBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appCwdBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appArgsBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appInitBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appMonitorBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appMatchProcessNameBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appMatchProcessPathBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appMatchTitleBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();
            appMatchAumidBox.TextChanged += (_, __) => UpdateSelectedAppFromFields();

            slotRoleBox.TextChanged += (_, __) => UpdateSelectedSlotFromFields();
            slotXBox.TextChanged += (_, __) => UpdateSelectedSlotFromFields();
            slotYBox.TextChanged += (_, __) => UpdateSelectedSlotFromFields();
            slotWBox.TextChanged += (_, __) => UpdateSelectedSlotFromFields();
            slotHBox.TextChanged += (_, __) => UpdateSelectedSlotFromFields();

            addAppButton.Click += (_, __) =>
            {
                appRows.Add(new TopToolbar.Services.Workspaces.TemplateWindowDefinition
                {
                    Role = "app",
                    Exe = "notepad.exe",
                    Monitor = "primary",
                    MatchHints = new TopToolbar.Services.Workspaces.TemplateMatchHints { ProcessName = "notepad" },
                });
                RefreshAppItems();
                appsList.SelectedIndex = appRows.Count - 1;
            };

            removeAppButton.Click += (_, __) =>
            {
                if (appsList.SelectedIndex >= 0 && appsList.SelectedIndex < appRows.Count)
                {
                    var index = appsList.SelectedIndex;
                    appRows.RemoveAt(index);
                    RefreshAppItems();
                    appsList.SelectedIndex = appRows.Count > 0 ? Math.Min(index, appRows.Count - 1) : -1;
                }
            };

            addSlotButton.Click += (_, __) =>
            {
                var role = appsList.SelectedIndex >= 0 && appsList.SelectedIndex < appRows.Count
                    ? (appRows[appsList.SelectedIndex].Role ?? "app")
                    : "app";
                slotRows.Add(new TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition
                {
                    Role = role,
                    X = 0,
                    Y = 0,
                    Width = 1,
                    Height = 1,
                });
                RefreshSlotItems();
                slotsList.SelectedIndex = slotRows.Count - 1;
            };

            removeSlotButton.Click += (_, __) =>
            {
                if (slotsList.SelectedIndex >= 0 && slotsList.SelectedIndex < slotRows.Count)
                {
                    var index = slotsList.SelectedIndex;
                    slotRows.RemoveAt(index);
                    RefreshSlotItems();
                    slotsList.SelectedIndex = slotRows.Count > 0 ? Math.Min(index, slotRows.Count - 1) : -1;
                }
            };

            var designerDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = $"Configure template: {working.Name}",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = true,
                Content = new ScrollViewer
                {
                    MaxHeight = 680,
                    MaxWidth = 980,
                    Content = content,
                },
            };

            displayNameBox.TextChanged += (_, __) => UpdateValidationState(designerDialog);
            descriptionBox.TextChanged += (_, __) => UpdateValidationState(designerDialog);
            defaultRepoBox.TextChanged += (_, __) => UpdateValidationState(designerDialog);
            focusPriorityBox.TextChanged += (_, __) => UpdateValidationState(designerDialog);
            monitorPolicyPicker.SelectionChanged += (_, __) => UpdateValidationState(designerDialog);
            strategyPicker.SelectionChanged += (_, __) => UpdateValidationState(designerDialog);
            requiresRepoToggle.Toggled += (_, __) => UpdateValidationState(designerDialog);

            RefreshAppItems();
            RefreshSlotItems();
            appsList.SelectedIndex = appRows.Count > 0 ? 0 : -1;
            slotsList.SelectedIndex = slotRows.Count > 0 ? 0 : -1;
            UpdateValidationState(designerDialog);

            var designerResult = await designerDialog.ShowAsync();
            if (designerResult != ContentDialogResult.Primary)
            {
                return false;
            }

            var finalCandidate = BuildCandidate();
            TopToolbar.Services.Workspaces.TemplateDefinitionStandardizer.StandardizeInPlace(finalCandidate);
            var finalErrors = TopToolbar.Services.Workspaces.TemplateDefinitionValidator.Validate(finalCandidate);
            if (finalErrors.Count > 0)
            {
                await ShowSimpleMessageOnUiThreadAsync(
                    xamlRoot,
                    "Validation failed",
                    string.Join(Environment.NewLine, finalErrors));
                return false;
            }

            await orchestrator.SaveTemplateAsync(finalCandidate, CancellationToken.None).ConfigureAwait(true);
            await ShowSimpleMessageOnUiThreadAsync(
                xamlRoot,
                "Template saved",
                $"Template '{finalCandidate.Name}' has been saved.");
            return true;
        }

        private static TopToolbar.Services.Workspaces.TemplateDefinition CloneTemplateDefinition(TopToolbar.Services.Workspaces.TemplateDefinition source)
        {
            if (source == null)
            {
                return new TopToolbar.Services.Workspaces.TemplateDefinition();
            }

            return new TopToolbar.Services.Workspaces.TemplateDefinition
            {
                SchemaVersion = source.SchemaVersion,
                Name = source.Name ?? string.Empty,
                DisplayName = source.DisplayName ?? string.Empty,
                Description = source.Description ?? string.Empty,
                RequiresRepo = source.RequiresRepo,
                DefaultRepoRoot = source.DefaultRepoRoot ?? string.Empty,
                FocusPriority = source.FocusPriority?.Select(item => item ?? string.Empty).ToList() ?? new List<string>(),
                Layout = new TopToolbar.Services.Workspaces.TemplateLayoutDefinition
                {
                    Strategy = source.Layout?.Strategy ?? string.Empty,
                    MonitorPolicy = source.Layout?.MonitorPolicy ?? string.Empty,
                    Preset = source.Layout?.Preset ?? string.Empty,
                    Slots = source.Layout?.Slots?.Select(CloneTemplateLayoutSlotDefinition).ToList() ?? new List<TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition>(),
                },
                Windows = source.Windows?.Select(CloneTemplateWindowDefinition).ToList() ?? new List<TopToolbar.Services.Workspaces.TemplateWindowDefinition>(),
                Agent = new TopToolbar.Services.Workspaces.TemplateAgentDefinition
                {
                    Enabled = source.Agent?.Enabled ?? false,
                    Name = source.Agent?.Name ?? "copilot",
                    Command = source.Agent?.Command ?? string.Empty,
                    WorkingDirectory = source.Agent?.WorkingDirectory ?? "{repo}",
                },
                Creation = new TopToolbar.Services.Workspaces.TemplateCreationDefinition
                {
                    CreateWorktreeByDefault = source.Creation?.CreateWorktreeByDefault ?? false,
                    WorktreeBaseBranch = source.Creation?.WorktreeBaseBranch ?? "main",
                },
            };
        }

        private static TopToolbar.Services.Workspaces.TemplateWindowDefinition CloneTemplateWindowDefinition(TopToolbar.Services.Workspaces.TemplateWindowDefinition source)
        {
            if (source == null)
            {
                return new TopToolbar.Services.Workspaces.TemplateWindowDefinition();
            }

            return new TopToolbar.Services.Workspaces.TemplateWindowDefinition
            {
                Role = source.Role ?? string.Empty,
                Exe = source.Exe ?? string.Empty,
                WorkingDirectory = source.WorkingDirectory ?? string.Empty,
                Args = source.Args ?? string.Empty,
                Init = source.Init ?? string.Empty,
                Monitor = source.Monitor ?? string.Empty,
                MatchHints = new TopToolbar.Services.Workspaces.TemplateMatchHints
                {
                    ProcessName = source.MatchHints?.ProcessName ?? string.Empty,
                    ProcessPath = source.MatchHints?.ProcessPath ?? string.Empty,
                    Title = source.MatchHints?.Title ?? string.Empty,
                    AppUserModelId = source.MatchHints?.AppUserModelId ?? string.Empty,
                },
            };
        }

        private static TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition CloneTemplateLayoutSlotDefinition(TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition source)
        {
            if (source == null)
            {
                return new TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition();
            }

            return new TopToolbar.Services.Workspaces.TemplateLayoutSlotDefinition
            {
                Role = source.Role ?? string.Empty,
                X = source.X,
                Y = source.Y,
                Width = source.Width,
                Height = source.Height,
                MinWidth = source.MinWidth,
                MinHeight = source.MinHeight,
            };
        }
    }
}
