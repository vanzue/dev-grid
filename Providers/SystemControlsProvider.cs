// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Actions;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services;
using Windows.Media.Control;
using Timer = System.Timers.Timer;

namespace TopToolbar.Providers
{
    public sealed class SystemControlsProvider : IActionProvider, IToolbarGroupProvider, IChangeNotifyingActionProvider, IDisposable
    {
        private const string ProviderName = "System Controls";
        private const string ProviderVersion = "1.0";
        private const string GroupId = "system-controls";
        private const string GroupName = "System Controls";
        private const string GroupDescription = "Built-in system controls";
        private const string MediaActionId = "media.playpause";
        private const string MediaButtonId = "system-controls::media-play-pause";
        private const string MediaPauseGlyph = "\uE769";
        private const string MediaPlayGlyph = "\uE768";
        private const string MediaUnavailableGlyph = "\uE711";
        private const int KeyEventKeyUp = 0x0002;
        private const int VkLeftWindows = 0x5B;
        private const int VkD = 0x44;
        private const int VkTab = 0x09;
        private const int WmSysCommand = 0x0112;
        private const int ScScreenSave = 0xF140;
        private const int ScMonitorPower = 0xF170;
        private const int MonitorPowerOff = 2;
        private static readonly IntPtr HwndBroadcast = new(0xFFFF);

        private readonly ToolbarConfigService _configService = new();
        private readonly SemaphoreSlim _initGate = new(1, 1);
        private readonly object _sessionLock = new();
        private readonly Timer _refreshDebounceTimer;
        private readonly TimeSpan _optimisticStateLifetime = TimeSpan.FromSeconds(2);
        private GlobalSystemMediaTransportControlsSessionManager _sessionManager;
        private GlobalSystemMediaTransportControlsSession _trackedSession;
        private bool _hasOptimisticIsPlaying;
        private bool _optimisticIsPlaying;
        private DateTime _optimisticSetUtc;
        private bool _disposed;

        public SystemControlsProvider()
        {
            _refreshDebounceTimer = new Timer(60) { AutoReset = false };
            _refreshDebounceTimer.Elapsed += (_, __) => RaiseGroupUpdated();
            _ = EnsureSessionManagerAsync(CancellationToken.None);
        }

        public string Id => "SystemControlsProvider";

        public event EventHandler<ProviderChangedEventArgs> ProviderChanged;

        public Task<ProviderInfo> GetInfoAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderInfo(ProviderName, ProviderVersion));
        }

        public async IAsyncEnumerable<ActionDescriptor> DiscoverAsync(
            ActionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (!state.SystemControlsEnabled)
            {
                yield break;
            }

            if (state.MediaPlayPauseEnabled)
            {
                yield return new ActionDescriptor
                {
                    Id = MediaActionId,
                    ProviderId = Id,
                    Title = "Media Play/Pause",
                    Subtitle = GetMediaDescription(state),
                    Kind = ActionKind.Command,
                    GroupHint = GroupId,
                    Order = 0,
                    Icon = new ActionIcon
                    {
                        Type = ActionIconType.Glyph,
                        Value = GetMediaGlyph(state),
                    },
                    CanExecute = state.HasSession,
                };
            }

            foreach (var descriptor in CreateSystemActionDescriptors())
            {
                yield return descriptor;
            }
        }

        private IEnumerable<ActionDescriptor> CreateSystemActionDescriptors()
        {
            yield return CreateSystemActionDescriptor(
                HotCornerActions.ShowDesktop,
                "Show desktop",
                "Minimize or restore desktop windows",
                "\uE80F",
                10);
            yield return CreateSystemActionDescriptor(
                HotCornerActions.TaskView,
                "Task view",
                "Open Windows task view",
                "\uE7C4",
                11);
            yield return CreateSystemActionDescriptor(
                HotCornerActions.LockScreen,
                "Lock screen",
                "Lock this PC",
                "\uE72E",
                12);
            yield return CreateSystemActionDescriptor(
                HotCornerActions.StartScreenSaver,
                "Start screen saver",
                "Start the configured screen saver",
                "\uE708",
                13);
            yield return CreateSystemActionDescriptor(
                HotCornerActions.TurnOffDisplay,
                "Turn off display",
                "Turn off the display",
                "\uE7F4",
                14);
        }

        private ActionDescriptor CreateSystemActionDescriptor(
            string actionId,
            string title,
            string subtitle,
            string glyph,
            double order)
        {
            return new ActionDescriptor
            {
                Id = actionId,
                ProviderId = Id,
                Title = title,
                Subtitle = subtitle,
                Kind = ActionKind.Command,
                GroupHint = GroupId,
                Order = order,
                Icon = new ActionIcon
                {
                    Type = ActionIconType.Glyph,
                    Value = glyph,
                },
                CanExecute = true,
            };
        }

        public async Task<ButtonGroup> CreateGroupAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            var group = CreateBaseGroup();

            if (!state.SystemControlsEnabled)
            {
                return group;
            }

            if (state.MediaPlayPauseEnabled)
            {
                group.Buttons.Add(new ToolbarButton
                {
                    Id = MediaButtonId,
                    Name = "Media",
                    Description = GetMediaDescription(state),
                    IconType = ToolbarIconType.Catalog,
                    IconGlyph = GetMediaGlyph(state),
                    IsDimmed = !state.HasSession,
                    IsEnabled = true,
                    Action = new ToolbarAction
                    {
                        Type = ToolbarActionType.Provider,
                        ProviderId = Id,
                        ProviderActionId = MediaActionId,
                    },
                });
            }

            foreach (var descriptor in CreateSystemActionDescriptors())
            {
                group.Buttons.Add(new ToolbarButton
                {
                    Id = $"system-controls::{descriptor.Id}",
                    Name = descriptor.Title,
                    Description = descriptor.Subtitle,
                    IconType = ToolbarIconType.Catalog,
                    IconGlyph = descriptor.Icon?.Value ?? "\uE7F4",
                    IsEnabled = true,
                    Surfaces = ActionSurfaces.Ring | ActionSurfaces.Corner,
                    Action = new ToolbarAction
                    {
                        Type = ToolbarActionType.Provider,
                        ProviderId = Id,
                        ProviderActionId = descriptor.Id,
                    },
                });
            }

            return group;
        }

        public async Task<ActionResult> InvokeAsync(
            string actionId,
            System.Text.Json.JsonElement? args,
            ActionContext context,
            IProgress<ActionProgress> progress,
            CancellationToken cancellationToken)
        {
            if (string.Equals(actionId, HotCornerActions.ShowDesktop, StringComparison.OrdinalIgnoreCase))
            {
                ShowDesktop();
                return new ActionResult { Ok = true };
            }

            if (string.Equals(actionId, HotCornerActions.TaskView, StringComparison.OrdinalIgnoreCase))
            {
                ShowTaskView();
                return new ActionResult { Ok = true };
            }

            if (string.Equals(actionId, HotCornerActions.LockScreen, StringComparison.OrdinalIgnoreCase))
            {
                LockScreen();
                return new ActionResult { Ok = true };
            }

            if (string.Equals(actionId, HotCornerActions.StartScreenSaver, StringComparison.OrdinalIgnoreCase))
            {
                StartScreenSaver();
                return new ActionResult { Ok = true };
            }

            if (string.Equals(actionId, HotCornerActions.TurnOffDisplay, StringComparison.OrdinalIgnoreCase))
            {
                TurnOffDisplay();
                return new ActionResult { Ok = true };
            }

            if (!string.Equals(actionId, MediaActionId, StringComparison.OrdinalIgnoreCase))
            {
                return new ActionResult
                {
                    Ok = false,
                    Message = "Unknown system-controls action.",
                };
            }

            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (!state.SystemControlsEnabled || !state.MediaPlayPauseEnabled)
            {
                return new ActionResult
                {
                    Ok = false,
                    Message = "System controls action is disabled in settings.",
                };
            }

            if (!state.HasSession || state.Session == null)
            {
                return new ActionResult
                {
                    Ok = true,
                    Message = "No active media session. Start playback and control here.",
                };
            }

            try
            {
                bool ok = state.IsPlaying
                    ? await state.Session.TryPauseAsync()
                    : await state.Session.TryPlayAsync();

                if (ok)
                {
                    SetOptimisticPlaybackState(!state.IsPlaying);
                    QueueRefresh(immediate: true);
                }
                else
                {
                    QueueRefresh();
                }

                return new ActionResult
                {
                    Ok = ok,
                    Message = ok ? string.Empty : "Media command failed.",
                };
            }
            catch (Exception ex)
            {
                QueueRefresh();
                return new ActionResult
                {
                    Ok = false,
                    Message = ex.Message,
                };
            }
        }

        private static void ShowDesktop()
        {
            AppLogger.LogInfo("SystemControls: show desktop.");
            SendWindowsShortcut(VkD);
        }

        private static void ShowTaskView()
        {
            AppLogger.LogInfo("SystemControls: task view.");
            SendWindowsShortcut(VkTab);
        }

        private static void LockScreen()
        {
            AppLogger.LogInfo("SystemControls: lock screen.");
            if (!LockWorkStation())
            {
                var error = Marshal.GetLastWin32Error();
                AppLogger.LogWarning($"SystemControls: LockWorkStation failed, error={error}.");
            }
        }

        private static void StartScreenSaver()
        {
            AppLogger.LogInfo("SystemControls: start screen saver.");
            PostMessage(HwndBroadcast, WmSysCommand, new IntPtr(ScScreenSave), IntPtr.Zero);
        }

        private static void TurnOffDisplay()
        {
            AppLogger.LogInfo("SystemControls: turn off display.");
            PostMessage(HwndBroadcast, WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(MonitorPowerOff));
        }

        private static void SendWindowsShortcut(int virtualKey)
        {
            keybd_event((byte)VkLeftWindows, 0, 0, UIntPtr.Zero);
            keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event((byte)virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
            keybd_event((byte)VkLeftWindows, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);

        private async Task<SystemControlsState> GetStateAsync(CancellationToken cancellationToken)
        {
            var cfg = await _configService.LoadAsync().ConfigureAwait(false);
            var defaultActions = cfg?.DefaultActions ?? new DefaultActionsConfig();
            var mediaConfig = defaultActions.MediaPlayPause ?? new DefaultActionItemConfig();

            await EnsureSessionManagerAsync(cancellationToken).ConfigureAwait(false);
            RefreshTrackedSession();

            GlobalSystemMediaTransportControlsSession session;
            bool optimisticIsPlaying;
            bool hasOptimisticState;
            lock (_sessionLock)
            {
                session = _trackedSession;
                hasOptimisticState = TryGetOptimisticPlaybackState_NoLock(out optimisticIsPlaying);
            }

            bool isPlaying = IsSessionPlaying(session);
            bool hasSession = session != null;
            if (hasSession && hasOptimisticState)
            {
                isPlaying = optimisticIsPlaying;
            }

            var nowPlaying = hasSession
                ? await GetNowPlayingTextAsync(session).ConfigureAwait(false)
                : string.Empty;

            return new SystemControlsState(
                defaultActions.SystemControlsEnabled,
                mediaConfig.Enabled,
                isPlaying,
                hasSession,
                session,
                nowPlaying);
        }

        private async Task EnsureSessionManagerAsync(CancellationToken cancellationToken)
        {
            if (_sessionManager != null || _disposed)
            {
                return;
            }

            await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_sessionManager != null || _disposed)
                {
                    return;
                }

                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (manager == null)
                {
                    return;
                }

                manager.CurrentSessionChanged += OnCurrentSessionChanged;
                manager.SessionsChanged += OnSessionsChanged;

                lock (_sessionLock)
                {
                    _sessionManager = manager;
                    AttachTrackedSession_NoLock(GetPreferredSession_NoLock());
                }
            }
            catch
            {
            }
            finally
            {
                _ = _initGate.Release();
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            RefreshTrackedSession();
            QueueRefresh();
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            RefreshTrackedSession();
            QueueRefresh();
        }

        private void OnTrackedSessionPlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender,
            PlaybackInfoChangedEventArgs args)
        {
            lock (_sessionLock)
            {
                ClearOptimisticPlaybackState_NoLock();
            }

            QueueRefresh();
        }

        private void OnTrackedSessionMediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            QueueRefresh();
        }

        private void QueueRefresh(bool immediate = false)
        {
            if (_disposed)
            {
                return;
            }

            if (immediate)
            {
                RaiseGroupUpdated();
                return;
            }

            try
            {
                _refreshDebounceTimer.Stop();
                _refreshDebounceTimer.Start();
            }
            catch
            {
            }
        }

        private void RaiseGroupUpdated()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ProviderChanged?.Invoke(this, ProviderChangedEventArgs.GroupUpdated(Id, new[] { GroupId }));
            }
            catch
            {
            }
        }

        private void RefreshTrackedSession()
        {
            lock (_sessionLock)
            {
                var current = GetPreferredSession_NoLock();
                if (ReferenceEquals(current, _trackedSession))
                {
                    return;
                }

                ClearOptimisticPlaybackState_NoLock();
                DetachTrackedSession_NoLock();
                AttachTrackedSession_NoLock(current);
            }
        }

        private void SetOptimisticPlaybackState(bool isPlaying)
        {
            lock (_sessionLock)
            {
                _hasOptimisticIsPlaying = true;
                _optimisticIsPlaying = isPlaying;
                _optimisticSetUtc = DateTime.UtcNow;
            }
        }

        private bool TryGetOptimisticPlaybackState_NoLock(out bool isPlaying)
        {
            if (!_hasOptimisticIsPlaying)
            {
                isPlaying = false;
                return false;
            }

            if (DateTime.UtcNow - _optimisticSetUtc > _optimisticStateLifetime)
            {
                ClearOptimisticPlaybackState_NoLock();
                isPlaying = false;
                return false;
            }

            isPlaying = _optimisticIsPlaying;
            return true;
        }

        private void ClearOptimisticPlaybackState_NoLock()
        {
            _hasOptimisticIsPlaying = false;
            _optimisticIsPlaying = false;
            _optimisticSetUtc = default;
        }

        private void AttachTrackedSession_NoLock(GlobalSystemMediaTransportControlsSession session)
        {
            _trackedSession = session;
            if (_trackedSession != null)
            {
                _trackedSession.PlaybackInfoChanged += OnTrackedSessionPlaybackInfoChanged;
                _trackedSession.MediaPropertiesChanged += OnTrackedSessionMediaPropertiesChanged;
            }
        }

        private void DetachTrackedSession_NoLock()
        {
            if (_trackedSession != null)
            {
                _trackedSession.PlaybackInfoChanged -= OnTrackedSessionPlaybackInfoChanged;
                _trackedSession.MediaPropertiesChanged -= OnTrackedSessionMediaPropertiesChanged;
                _trackedSession = null;
            }
        }

        private GlobalSystemMediaTransportControlsSession GetPreferredSession_NoLock()
        {
            if (_sessionManager == null)
            {
                return null;
            }

            var current = _sessionManager.GetCurrentSession();
            if (IsSessionPlaying(current))
            {
                return current;
            }

            try
            {
                var sessions = _sessionManager.GetSessions();
                if (sessions != null)
                {
                    foreach (var session in sessions)
                    {
                        if (IsSessionPlaying(session))
                        {
                            return session;
                        }
                    }
                }
            }
            catch
            {
            }

            return current;
        }

        private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null)
            {
                return false;
            }

            try
            {
                var info = session.GetPlaybackInfo();
                return info != null &&
                       info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        private static string GetMediaDescription(SystemControlsState state)
        {
            if (!state.HasSession || state.Session == null)
            {
                return "No active media. Start playback and control here.";
            }

            var status = state.IsPlaying
                ? "Media is playing. Click to pause."
                : "Media is paused. Click to play.";

            if (string.IsNullOrWhiteSpace(state.NowPlaying))
            {
                return status;
            }

            return $"{state.NowPlaying}{Environment.NewLine}{status}";
        }

        private static string GetMediaGlyph(SystemControlsState state)
        {
            if (!state.HasSession || state.Session == null)
            {
                return MediaUnavailableGlyph;
            }

            return state.IsPlaying ? MediaPauseGlyph : MediaPlayGlyph;
        }

        private static async Task<string> GetNowPlayingTextAsync(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null)
            {
                return string.Empty;
            }

            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props == null)
                {
                    return string.Empty;
                }

                var title = (props.Title ?? string.Empty).Trim();
                var artist = (props.Artist ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                {
                    return $"{title} - {artist}";
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }

                if (!string.IsNullOrWhiteSpace(artist))
                {
                    return artist;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static ButtonGroup CreateBaseGroup()
        {
            return new ButtonGroup
            {
                Id = GroupId,
                Name = GroupName,
                Description = GroupDescription,
                IsEnabled = true,
                Layout = new ToolbarGroupLayout
                {
                    Style = ToolbarGroupLayoutStyle.Icon,
                    Overflow = ToolbarGroupOverflowMode.Wrap,
                },
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _refreshDebounceTimer.Stop();
                _refreshDebounceTimer.Dispose();
            }
            catch
            {
            }

            lock (_sessionLock)
            {
                DetachTrackedSession_NoLock();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _sessionManager.SessionsChanged -= OnSessionsChanged;
                    _sessionManager = null;
                }
            }

            try
            {
                _initGate.Dispose();
            }
            catch
            {
            }

            ProviderChanged = null;
            GC.SuppressFinalize(this);
        }

        private sealed record SystemControlsState(
            bool SystemControlsEnabled,
            bool MediaPlayPauseEnabled,
            bool IsPlaying,
            bool HasSession,
            GlobalSystemMediaTransportControlsSession Session,
            string NowPlaying);
    }
}
