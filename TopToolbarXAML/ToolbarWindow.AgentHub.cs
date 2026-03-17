// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Services.Agents;
using TopToolbar.Services.Workspaces;
using TopToolbar.Logging;
using Windows.UI;
using WinUIEx;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private sealed class AgentTemplateOption
        {
            public string Name { get; set; } = string.Empty;

            public string DisplayName { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;
        }

        private sealed class AgentSessionChipItem
        {
            public string SessionId { get; set; } = string.Empty;

            public string ShortDisplayName { get; set; } = string.Empty;

            public string ToolTip { get; set; } = string.Empty;

            public string StateGlyph { get; set; } = "\uE9CE";

            public Brush StateBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0xFF, 0x7C, 0x7C, 0x7C));
        }

        private ObservableCollection<AgentSessionChipItem> AgentSessionChips { get; } = new();

        private bool _agentLaunchInProgress;

        private void OnAgentSessionChanged(object sender, AgentSessionChangedEventArgs e)
        {
            if (DispatcherQueue == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                UpdateAgentHubVisualState();
                ShowAgentSessionToast(e);
            });
        }

        private void UpdateAgentHubVisualState()
        {
            if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
            {
                _ = DispatcherQueue.TryEnqueue(UpdateAgentHubVisualState);
                return;
            }

            var sessions = _agentSessionManager?.GetSessionsSnapshot() ?? Array.Empty<AgentSessionRecord>();
            var activeCount = sessions.Count(session =>
                session.State == AgentSessionState.Running || session.State == AgentSessionState.WaitingUser);
            var waitingCount = sessions.Count(session => session.State == AgentSessionState.WaitingUser);
            var errorCount = sessions.Count(session => session.State == AgentSessionState.Error);
            var previousChipCount = AgentSessionChips.Count;
            RefreshAgentSessionChips(sessions);
            var chipCountChanged = previousChipCount != AgentSessionChips.Count;

            if (AgentBadgeBorder != null)
            {
                AgentBadgeBorder.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                AgentBadgeBorder.Background = new SolidColorBrush(
                    waitingCount > 0
                        ? Color.FromArgb(0xFF, 0xF2, 0xC8, 0x11)
                        : Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));
            }

            if (AgentBadgeText != null)
            {
                AgentBadgeText.Text = activeCount > 99 ? "99+" : activeCount.ToString();
            }

            if (AgentHubIcon != null)
            {
                AgentHubIcon.Opacity = _agentLaunchInProgress ? 0.6d : 1d;
            }

            if (AgentHubLabel != null)
            {
                AgentHubLabel.Opacity = _agentLaunchInProgress ? 0.6d : 1d;
            }

            if (AgentHubButton != null)
            {
                ToolTipService.SetToolTip(
                    AgentHubButton,
                    $"Agent sessions: active={activeCount}, waiting={waitingCount}, error={errorCount}");
                AgentHubButton.IsEnabled = !_agentLaunchInProgress;
            }

            if (chipCountChanged && _currentDisplayMode == Models.ToolbarDisplayMode.TopBar)
            {
                ResizeToContent();
                if (_isVisible)
                {
                    ShowToolbar();
                }
                else
                {
                    PositionAtTopCenter();
                }
            }
        }

        private void ShowAgentSessionToast(AgentSessionChangedEventArgs args)
        {
            if (args?.Session == null)
            {
                return;
            }

            var session = args.Session;
            if (session.State == AgentSessionState.WaitingUser
                && args.PreviousState != AgentSessionState.WaitingUser)
            {
                _notificationService.ShowInfo($"Agent '{session.DisplayName}' is waiting for input.");
                return;
            }

            if (session.State == AgentSessionState.Done
                && args.PreviousState != AgentSessionState.Done)
            {
                _notificationService.ShowSuccess($"Agent '{session.DisplayName}' completed.");
                return;
            }

            if (session.State == AgentSessionState.Error
                && args.PreviousState != AgentSessionState.Error)
            {
                var message = string.IsNullOrWhiteSpace(session.StateMessage)
                    ? "Session failed."
                    : session.StateMessage;
                _notificationService.ShowError($"Agent '{session.DisplayName}' failed: {message}");
                return;
            }

            if (session.State == AgentSessionState.Ended
                && args.PreviousState != AgentSessionState.Ended)
            {
                _notificationService.ShowInfo($"Agent '{session.DisplayName}' window closed.");
            }
        }

        private void OnAgentHubClick(object sender, RoutedEventArgs e)
        {
            _ = LaunchAgentFromTemplateAsync();
        }

        private void OnAgentSessionChipClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: AgentSessionChipItem chip })
            {
                return;
            }

            if (!_agentSessionManager.TryFocusSession(chip.SessionId, out var message))
            {
                var details = string.IsNullOrWhiteSpace(message)
                    ? "Window not found."
                    : message;
                _notificationService.ShowError($"Unable to focus '{chip.ShortDisplayName}': {details}");
            }
        }

        private void OnAgentSessionChipRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement { Tag: AgentSessionChipItem chip })
            {
                return;
            }

            var session = (_agentSessionManager?.GetSessionsSnapshot() ?? Array.Empty<AgentSessionRecord>())
                .FirstOrDefault(item => string.Equals(item.SessionId, chip.SessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return;
            }

            var flyout = new MenuFlyout();
            var focusItem = new MenuFlyoutItem
            {
                Text = "Focus",
                Icon = new FontIcon { Glyph = "\uE8A7" },
            };
            focusItem.Click += (_, __) =>
            {
                if (!_agentSessionManager.TryFocusSession(session.SessionId, out var message))
                {
                    var details = string.IsNullOrWhiteSpace(message)
                        ? "Window not found."
                        : message;
                    _notificationService.ShowError($"Unable to focus '{session.DisplayName}': {details}");
                }
            };
            flyout.Items.Add(focusItem);

            var terminateItem = new MenuFlyoutItem
            {
                Text = "Terminate",
                Icon = new FontIcon { Glyph = "\uE711" },
                IsEnabled = session.IsActive,
            };
            terminateItem.Click += (_, __) =>
            {
                if (!_agentSessionManager.TerminateSession(session.SessionId))
                {
                    _notificationService.ShowError($"Unable to terminate '{session.DisplayName}'.");
                }
            };
            flyout.Items.Add(terminateItem);

            var archiveItem = new MenuFlyoutItem
            {
                Text = "Archive",
                Icon = new FontIcon { Glyph = "\uE74D" },
            };
            archiveItem.Click += (_, __) =>
            {
                _ = _agentSessionManager.ArchiveSession(session.SessionId);
                UpdateAgentHubVisualState();
            };
            flyout.Items.Add(archiveItem);
            flyout.ShowAt(sender as FrameworkElement);
        }

        private async Task LaunchAgentFromTemplateAsync()
        {
            if (_agentLaunchInProgress)
            {
                return;
            }

            _agentLaunchInProgress = true;
            UpdateAgentHubVisualState();
            AppLogger.LogInfo("AgentHub: launch flow started.");

            try
            {
                var templates = await LoadAgentTemplateOptionsAsync().ConfigureAwait(true);
                AppLogger.LogInfo($"AgentHub: loaded {templates.Count} agent template option(s).");
                if (templates.Count == 0)
                {
                    _notificationService.ShowError("No agent templates are available. Configure one in Templates settings.");
                    return;
                }

                var selected = await RunOnUiThreadAsync(() => PromptSelectAgentTemplateAsync(this, templates))
                    .ConfigureAwait(false);
                if (selected == null || string.IsNullOrWhiteSpace(selected.Name))
                {
                    AppLogger.LogInfo("AgentHub: launch canceled in template picker.");
                    return;
                }

                var initialTask = await RunOnUiThreadAsync(() => _toastWindow.ShowInputPromptAsync(
                        "Start agent task",
                        "Describe what this agent should do first. This task is sent to the agent immediately.",
                        "Example: fix issue https://github.com/microsoft/PowerToys/issues/45929",
                        initialValue: string.Empty,
                        fieldLabel: "Initial task",
                        confirmButtonText: "Start agent",
                        subtitle: "Launch a new agent session from the selected template."))
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(initialTask))
                {
                    AppLogger.LogInfo("AgentHub: launch canceled in task prompt.");
                    return;
                }

                AppLogger.LogInfo($"AgentHub: launching template '{selected.Name}'.");
                var progressId = _notificationService.ShowProgress("Launching agent...");
                var result = await _agentSessionManager
                    .StartSessionFromTemplateAsync(selected.Name, initialTask, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!result.Success)
                {
                    AppLogger.LogWarning($"AgentHub: launch failed for template '{selected.Name}': {result.Message}");
                    _notificationService.CompleteProgress(progressId, result.Message, isError: true);
                    return;
                }

                AppLogger.LogInfo(
                    $"AgentHub: launch succeeded. sessionId='{result.Session?.SessionId}', display='{result.Session?.DisplayName}'.");
                _notificationService.CompleteProgress(
                    progressId,
                    $"Agent started: {result.Session.DisplayName}");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("AgentHub: launch flow exception.", ex);
                _notificationService.ShowError($"Agent launch failed: {ex.Message}");
            }
            finally
            {
                _agentLaunchInProgress = false;
                UpdateAgentHubVisualState();
                AppLogger.LogInfo("AgentHub: launch flow ended.");
            }
        }

        private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
        {
            if (action == null)
            {
                return Task.FromResult(default(T));
            }

            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<T>();
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var result = await action().ConfigureAwait(true);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetCanceled();
            }

            return tcs.Task;
        }

        private static async Task<AgentTemplateOption> PromptSelectAgentTemplateAsync(
            WindowEx owner,
            IReadOnlyList<AgentTemplateOption> templates)
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
            var picker = new ComboBox
            {
                MinWidth = 320,
                ItemsSource = templates,
                DisplayMemberPath = nameof(AgentTemplateOption.DisplayName),
                PlaceholderText = "Select template",
                SelectedIndex = 0,
            };

            var dialog = new ContentDialog
            {
                XamlRoot = overlay.Root.XamlRoot,
                Title = "Launch agent",
                PrimaryButtonText = "Launch",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = picker,
                IsPrimaryButtonEnabled = true,
            };

            var result = await dialog.ShowAsync(ContentDialogPlacement.Popup);
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            return picker.SelectedItem as AgentTemplateOption ?? templates[0];
        }

        private static async Task<IReadOnlyList<AgentTemplateOption>> LoadAgentTemplateOptionsAsync()
        {
            using var orchestrator = new WorkspaceTemplateOrchestrator();
            var templates = await orchestrator.ListTemplatesAsync(CancellationToken.None).ConfigureAwait(false);
            return templates
                .Where(template => template != null
                                   && !string.IsNullOrWhiteSpace(template.Name)
                                   && string.Equals(
                                       TemplateDefinitionValidator.NormalizeKind(template.Kind),
                                       "agent",
                                       StringComparison.OrdinalIgnoreCase)
                                   && template.Agent?.Enabled == true
                                   && (!string.IsNullOrWhiteSpace(template.Agent.Command)
                                       || !string.IsNullOrWhiteSpace(template.Agent.Name)))
                .Select(template => new AgentTemplateOption
                {
                    Name = template.Name,
                    DisplayName = string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName,
                    Description = string.IsNullOrWhiteSpace(template.Description)
                        ? $"backend: {template.Agent?.Name ?? "custom"}"
                        : template.Description,
                })
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatStateLabel(AgentSessionState state)
        {
            return state switch
            {
                AgentSessionState.WaitingUser => "Waiting User",
                AgentSessionState.Starting => "Starting",
                AgentSessionState.Running => "Running",
                AgentSessionState.Done => "Done",
                AgentSessionState.Error => "Error",
                AgentSessionState.Cancelled => "Cancelled",
                AgentSessionState.Ended => "Ended",
                _ => state.ToString(),
            };
        }

        private static string SelectStateGlyph(AgentSessionState state)
        {
            return state switch
            {
                AgentSessionState.WaitingUser => "\uE8FD",
                AgentSessionState.Starting => "\uE895",
                AgentSessionState.Running => "\uE768",
                AgentSessionState.Done => "\uE73E",
                AgentSessionState.Error => "\uEA39",
                AgentSessionState.Cancelled => "\uE711",
                AgentSessionState.Ended => "\uE7E8",
                _ => "\uE9CE",
            };
        }

        private void RefreshAgentSessionChips(IReadOnlyList<AgentSessionRecord> sessions)
        {
            sessions ??= Array.Empty<AgentSessionRecord>();
            var ordered = sessions
                .Where(session => session != null && !string.IsNullOrWhiteSpace(session.SessionId))
                .OrderBy(session => session.IsActive ? 0 : 1)
                .ThenByDescending(session => session.LastUpdatedAt)
                .Take(4)
                .ToList();

            AgentSessionChips.Clear();
            foreach (var session in ordered)
            {
                var displayName = string.IsNullOrWhiteSpace(session.DisplayName)
                    ? "Agent"
                    : session.DisplayName.Trim();
                var stateLabel = FormatStateLabel(session.State);
                AgentSessionChips.Add(new AgentSessionChipItem
                {
                    SessionId = session.SessionId,
                    ShortDisplayName = Truncate(displayName, 12),
                    ToolTip = $"{displayName} · {stateLabel}",
                    StateGlyph = SelectStateGlyph(session.State),
                    StateBrush = CreateStateBrush(session.State),
                });
            }
        }

        private static string Truncate(string value, int length)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length <= length)
            {
                return text;
            }

            return text.Substring(0, Math.Max(1, length - 1)).TrimEnd() + "\u2026";
        }

        private static Brush CreateStateBrush(AgentSessionState state)
        {
            var color = state switch
            {
                AgentSessionState.Running => Color.FromArgb(0xFF, 0x10, 0x7C, 0x10),
                AgentSessionState.WaitingUser => Color.FromArgb(0xFF, 0xF2, 0xC8, 0x11),
                AgentSessionState.Done => Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43),
                AgentSessionState.Error => Color.FromArgb(0xFF, 0xD1, 0x34, 0x38),
                AgentSessionState.Starting => Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
                AgentSessionState.Cancelled => Color.FromArgb(0xFF, 0x7C, 0x7C, 0x7C),
                AgentSessionState.Ended => Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A),
                _ => Color.FromArgb(0xFF, 0x7C, 0x7C, 0x7C),
            };
            return new SolidColorBrush(color);
        }
    }
}
