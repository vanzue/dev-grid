// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Windowing;
using TopToolbar.Services.Workspaces;

namespace TopToolbar.Services.Agents
{
    internal sealed class AgentSessionManager : IDisposable
    {
        private readonly record struct AgentWorktreeCreationResult(bool Success, string WorktreePath, string BranchName, string Message);
        private sealed class DirectSessionMonitorState
        {
            public bool IsCopilot { get; init; }

            public string BackendProcessName { get; init; } = string.Empty;

            public int BackendProcessId { get; set; }

            public string CopilotLogPath { get; set; } = string.Empty;

            public long CopilotLogOffset { get; set; }

            public int CopilotToolCallContextLines { get; set; }
        }

        private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(9);
        private static readonly TimeSpan WindowClosedGracePeriod = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LaunchFocusTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LaunchFocusPollInterval = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan BackendDiscoveryTimeout = TimeSpan.FromSeconds(12);

        private readonly AgentHubPipeServer _pipeServer;
        private readonly TemplateStore _templateStore;
        private readonly Dictionary<string, AgentSessionRecord> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DirectSessionMonitorState> _directMonitors = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private Timer _healthTimer;
        private bool _disposed;

        public AgentSessionManager(string pipeName = AgentHubProtocol.PipeName)
        {
            _pipeServer = new AgentHubPipeServer(pipeName);
            _pipeServer.EventReceived += OnPipeEventReceived;
            _templateStore = new TemplateStore();
        }

        public event EventHandler<AgentSessionChangedEventArgs> SessionChanged;

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            _pipeServer.Start();
            _healthTimer ??= new Timer(_ => MonitorSessionHealth(), null, MonitorInterval, MonitorInterval);
        }

        public IReadOnlyList<AgentSessionRecord> GetSessionsSnapshot()
        {
            lock (_gate)
            {
                return _sessions.Values
                    .Select(session => session.Clone())
                    .OrderByDescending(session => session.CreatedAt)
                    .ToList();
            }
        }

        public async Task<(bool Success, string Message, AgentSessionRecord Session)> StartSessionFromTemplateAsync(
            string templateName,
            string initialInput,
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return (false, "Agent session manager is disposed.", null);
            }

            var normalizedTemplate = WorkspaceStoragePaths.NormalizeTemplateName(templateName);
            if (string.IsNullOrWhiteSpace(normalizedTemplate))
            {
                return (false, "Template name is required.", null);
            }

            AppLogger.LogInfo($"AgentSessionManager: starting session from template '{normalizedTemplate}'.");

            var template = await _templateStore.LoadByNameAsync(normalizedTemplate, cancellationToken).ConfigureAwait(false);
            if (template == null)
            {
                return (false, $"Template '{normalizedTemplate}' was not found.", null);
            }

            if (!string.Equals(
                TemplateDefinitionValidator.NormalizeKind(template.Kind),
                "agent",
                StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Template '{normalizedTemplate}' is not an agent template.", null);
            }

            var prepared = await PrepareTemplateLaunchAsync(template, initialInput, cancellationToken).ConfigureAwait(false);
            if (!prepared.Success)
            {
                AppLogger.LogWarning(
                    $"AgentSessionManager: template '{normalizedTemplate}' launch preparation failed: {prepared.Message}");
                return (false, prepared.Message, null);
            }

            var session = new AgentSessionRecord
            {
                SessionId = Guid.NewGuid().ToString("N"),
                TemplateId = template.Name,
                DisplayName = prepared.DisplayName,
                Backend = prepared.Backend,
                WorkingDir = prepared.WorkingDirectory,
                CreatedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                State = AgentSessionState.Starting,
                StateMessage = "Launching terminal...",
            };

            RegisterDirectMonitor(session, prepared);
            UpsertSession(session, previousState: session.State);

            var launch = LaunchAgentInWindowsTerminal(session, prepared, initialInput);
            if (!launch.Success)
            {
                UnregisterDirectMonitor(session.SessionId);
                AppLogger.LogWarning(
                    $"AgentSessionManager: terminal launch failed for session '{session.SessionId}': {launch.Message}");
                session.State = AgentSessionState.Error;
                session.StateMessage = launch.Message;
                session.LastUpdatedAt = DateTimeOffset.UtcNow;
                UpsertSession(session, previousState: AgentSessionState.Starting);
                return (false, launch.Message, session.Clone());
            }

            session.WtProcessId = launch.WtProcessId;
            session.StateMessage = "Waiting for backend process...";
            session.LastUpdatedAt = DateTimeOffset.UtcNow;
            UpsertSession(session, previousState: AgentSessionState.Starting);

            AppLogger.LogInfo(
                $"AgentSessionManager: terminal launched. session='{session.SessionId}', wtPid={session.WtProcessId}, backend='{session.Backend}', cwd='{session.WorkingDir}'.");

            _ = FocusSessionWhenReadyAsync(session.SessionId, LaunchFocusTimeout, CancellationToken.None);

            return (true, "Agent session started.", session.Clone());
        }

        public bool FocusSession(string sessionId)
        {
            return TryFocusSession(sessionId, out _);
        }

        public bool TryFocusSession(string sessionId, out string message)
        {
            return TryFocusSessionInternal(sessionId, out message, logFailures: true);
        }

        private bool TryFocusSessionInternal(string sessionId, out string message, bool logFailures)
        {
            message = string.Empty;
            if (!TryGetSession(sessionId, out var session))
            {
                message = "Session not found.";
                if (logFailures)
                {
                    AppLogger.LogWarning($"AgentSessionManager: focus failed. session='{sessionId}', reason='{message}'");
                }

                return false;
            }

            var hwnd = ResolveSessionWindowHandle(session, out var resolveReason);
            if (hwnd == IntPtr.Zero)
            {
                message = string.IsNullOrWhiteSpace(resolveReason)
                    ? "Window handle not found."
                    : resolveReason;
                if (logFailures)
                {
                    AppLogger.LogWarning(
                        $"AgentSessionManager: focus failed. session='{session.SessionId}', display='{session.DisplayName}', reason='{message}', wtPid={session.WtProcessId}, wtHwnd=0x{session.WtWindowHwnd:X}.");
                }

                return false;
            }

            var focused = NativeWindowHelper.TryActivateWindow(hwnd);
            if (!focused)
            {
                _ = NativeWindowHelper.TryMoveWindowToCurrentVirtualDesktop(hwnd);
                focused = NativeWindowHelper.TryActivateWindow(hwnd);
            }

            if (!focused)
            {
                message = "Could not activate the window.";
                if (logFailures)
                {
                    AppLogger.LogWarning(
                        $"AgentSessionManager: focus failed. session='{session.SessionId}', display='{session.DisplayName}', reason='{message}', hwnd=0x{unchecked((ulong)hwnd.ToInt64()):X}.");
                }

                return false;
            }

            var previousState = session.State;
            session.WtWindowHwnd = (ulong)hwnd.ToInt64();
            if (NativeWindowHelper.TryCreateWindowInfo(hwnd, out var info) && info?.ProcessId > 0)
            {
                session.WtProcessId = (int)info.ProcessId;
            }

            if (session.State == AgentSessionState.Ended
                && string.Equals(session.StateMessage, "Terminal window closed.", StringComparison.OrdinalIgnoreCase))
            {
                session.State = AgentSessionState.Running;
                session.StateMessage = "Terminal is running.";
            }

            session.LastUpdatedAt = DateTimeOffset.UtcNow;
            UpsertSession(session, previousState);
            message = "Focused.";
            AppLogger.LogInfo(
                $"AgentSessionManager: focus succeeded. session='{session.SessionId}', display='{session.DisplayName}', hwnd=0x{unchecked((ulong)hwnd.ToInt64()):X}, wtPid={session.WtProcessId}.");
            return true;
        }

        private async Task FocusSessionWhenReadyAsync(
            string sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            var effectiveTimeout = timeout <= TimeSpan.Zero ? LaunchFocusTimeout : timeout;
            var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
            var lastReason = string.Empty;
            while (!_disposed
                   && !cancellationToken.IsCancellationRequested
                   && DateTimeOffset.UtcNow < deadline)
            {
                if (TryFocusSessionInternal(sessionId, out var message, logFailures: false))
                {
                    AppLogger.LogInfo($"AgentSessionManager: auto-focus succeeded. session='{sessionId}'.");
                    return;
                }

                lastReason = message ?? string.Empty;
                try
                {
                    await Task.Delay(LaunchFocusPollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(lastReason))
            {
                AppLogger.LogWarning(
                    $"AgentSessionManager: auto-focus timed out. session='{sessionId}', reason='{lastReason}'.");
            }
            else
            {
                AppLogger.LogWarning($"AgentSessionManager: auto-focus timed out. session='{sessionId}'.");
            }
        }

        public bool ArchiveSession(string sessionId)
        {
            var key = (sessionId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            AgentSessionRecord removed = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(key, out var existing))
                {
                    removed = existing.Clone();
                    _sessions.Remove(key);
                }

                _directMonitors.Remove(key);
            }

            if (removed == null)
            {
                return false;
            }

            RaiseSessionChanged(removed, removed.State);
            return true;
        }

        public bool TerminateSession(string sessionId)
        {
            if (!TryGetSession(sessionId, out var session))
            {
                return false;
            }

            var terminated = false;
            if (session.BackendProcessId > 0)
            {
                terminated = TryKillProcess(session.BackendProcessId);
            }

            if (!terminated && session.WtProcessId > 0)
            {
                terminated = TryKillProcess(session.WtProcessId);
            }

            if (terminated)
            {
                var previous = session.State;
                session.State = AgentSessionState.Cancelled;
                session.StateMessage = "Terminated by user.";
                session.LastUpdatedAt = DateTimeOffset.UtcNow;
                UpsertSession(session, previous);
                UnregisterDirectMonitor(session.SessionId);
            }

            return terminated;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _healthTimer?.Dispose();
            _healthTimer = null;
            lock (_gate)
            {
                _directMonitors.Clear();
            }

            _pipeServer.EventReceived -= OnPipeEventReceived;
            _pipeServer.Dispose();
            GC.SuppressFinalize(this);
        }

        private void MonitorSessionHealth()
        {
            if (_disposed)
            {
                return;
            }

            List<(AgentSessionRecord Session, AgentSessionState Previous)> updates = null;
            var now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                foreach (var existing in _sessions.Values)
                {
                    try
                    {
                        if (!existing.IsActive)
                        {
                            _directMonitors.Remove(existing.SessionId);
                            continue;
                        }

                        var previous = existing.State;
                        var changed = false;
                        var hasDirectMonitor = _directMonitors.TryGetValue(existing.SessionId, out var directMonitor);

                        if (hasDirectMonitor && ApplyDirectMonitorState(existing, directMonitor, now))
                        {
                            changed = true;
                        }

                        if (existing.WtProcessId > 0 && !ProcessTreeHelper.ProcessExists(existing.WtProcessId))
                        {
                            var rebound = TryRebindTerminalWindow(existing, now, out var reboundChanged);
                            changed |= reboundChanged;
                            if (!rebound && IsTerminalClosureGraceElapsed(existing, now))
                            {
                                existing.State = AgentSessionState.Ended;
                                existing.StateMessage = "Terminal window closed.";
                                changed = true;
                            }
                        }
                        else if (existing.WtWindowHwnd != 0)
                        {
                            var hwnd = new IntPtr(unchecked((long)existing.WtWindowHwnd));
                            if (!NativeWindowHelper.IsWindowHandleValid(hwnd))
                            {
                                var rebound = TryRebindTerminalWindow(existing, now, out var reboundChanged);
                                changed |= reboundChanged;
                                if (!rebound && IsTerminalClosureGraceElapsed(existing, now))
                                {
                                    existing.State = AgentSessionState.Ended;
                                    existing.StateMessage = "Terminal window closed.";
                                    changed = true;
                                }
                            }
                        }

                        if (!changed
                            && !hasDirectMonitor
                            && existing.LastHeartbeatAt != DateTimeOffset.MinValue
                            && now - existing.LastHeartbeatAt >= StaleThreshold)
                        {
                            existing.State = AgentSessionState.Error;
                            existing.StateMessage = "Agent heartbeat timed out.";
                            changed = true;
                        }

                        if (!changed)
                        {
                            continue;
                        }

                        if (existing.State != previous)
                        {
                            AppLogger.LogInfo(
                                $"AgentSessionManager: session '{existing.SessionId}' state {previous} -> {existing.State} ({existing.StateMessage}).");
                        }

                        existing.LastUpdatedAt = now;
                        updates ??= new List<(AgentSessionRecord, AgentSessionState)>();
                        updates.Add((existing.Clone(), previous));
                        if (!existing.IsActive)
                        {
                            _directMonitors.Remove(existing.SessionId);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning(
                            $"AgentSessionManager: health monitor iteration failed for session '{existing?.SessionId ?? "<unknown>"}' ({ex.GetType().Name}: {ex.Message}).");
                    }
                }
            }

            if (updates == null || updates.Count == 0)
            {
                return;
            }

            foreach (var update in updates)
            {
                RaiseSessionChanged(update.Session, update.Previous);
            }
        }

        private static bool ApplyDirectMonitorState(
            AgentSessionRecord session,
            DirectSessionMonitorState monitor,
            DateTimeOffset now)
        {
            if (session == null || monitor == null)
            {
                return false;
            }

            var changed = false;
            session.LastHeartbeatAt = now;

            if (monitor.BackendProcessId <= 0)
            {
                var discovered = TryDiscoverBackendProcessId(session, monitor);
                if (discovered > 0)
                {
                    monitor.BackendProcessId = discovered;
                    if (session.BackendProcessId != discovered)
                    {
                        session.BackendProcessId = discovered;
                        changed = true;
                    }

                    if (session.State == AgentSessionState.Starting)
                    {
                        session.State = AgentSessionState.Running;
                        session.StateMessage = monitor.IsCopilot
                            ? "Copilot started."
                            : "Agent started.";
                        changed = true;
                    }
                }
                else if (session.State == AgentSessionState.Starting
                         && monitor.IsCopilot)
                {
                    var fallbackLogPath = FindCopilotLogPathByWorkingDirectory(session.WorkingDir, session.CreatedAt);
                    if (!string.IsNullOrWhiteSpace(fallbackLogPath))
                    {
                        monitor.CopilotLogPath = fallbackLogPath;
                        monitor.CopilotLogOffset = 0;
                        monitor.CopilotToolCallContextLines = 0;
                        var pidFromLog = ParseCopilotProcessIdFromLogPath(fallbackLogPath);
                        if (pidFromLog > 0)
                        {
                            monitor.BackendProcessId = pidFromLog;
                            if (session.BackendProcessId != pidFromLog)
                            {
                                session.BackendProcessId = pidFromLog;
                                changed = true;
                            }
                        }

                        session.State = AgentSessionState.Running;
                        session.StateMessage = "Copilot started.";
                        changed = true;
                        AppLogger.LogInfo(
                            $"AgentSessionManager: attached copilot log by working directory for session '{session.SessionId}', pid={monitor.BackendProcessId}, path='{fallbackLogPath}'.");
                    }
                }

                if (session.State == AgentSessionState.Starting
                    && now - session.CreatedAt >= BackendDiscoveryTimeout)
                {
                    session.State = AgentSessionState.Running;
                    session.StateMessage = "Backend process not resolved yet; tracking terminal state.";
                    changed = true;
                }
            }
            else
            {
                if (session.BackendProcessId != monitor.BackendProcessId)
                {
                    session.BackendProcessId = monitor.BackendProcessId;
                    changed = true;
                }

                if (!ProcessTreeHelper.ProcessExists(monitor.BackendProcessId))
                {
                    session.State = AgentSessionState.Ended;
                    session.StateMessage = "Agent process exited.";
                    changed = true;
                    return changed;
                }
            }

            if (!monitor.IsCopilot
                || (monitor.BackendProcessId <= 0
                    && string.IsNullOrWhiteSpace(monitor.CopilotLogPath)))
            {
                return changed;
            }

            if (string.IsNullOrWhiteSpace(monitor.CopilotLogPath))
            {
                var discoveredPath = FindCopilotLogPathByProcessId(monitor.BackendProcessId);
                if (string.IsNullOrWhiteSpace(discoveredPath) && session.WtProcessId > 0)
                {
                    var copilotPid = ProcessTreeHelper.FindDescendantProcessIdByName(session.WtProcessId, "copilot");
                    if (copilotPid > 0 && copilotPid != monitor.BackendProcessId)
                    {
                        var previousPid = monitor.BackendProcessId;
                        monitor.BackendProcessId = copilotPid;
                        if (session.BackendProcessId != copilotPid)
                        {
                            session.BackendProcessId = copilotPid;
                            changed = true;
                        }

                        AppLogger.LogInfo(
                            $"AgentSessionManager: remapped backend pid for copilot session '{session.SessionId}' from {previousPid} to {copilotPid}.");

                        discoveredPath = FindCopilotLogPathByProcessId(copilotPid);
                    }
                }

                if (!string.IsNullOrWhiteSpace(discoveredPath))
                {
                    monitor.CopilotLogPath = discoveredPath;
                    monitor.CopilotLogOffset = 0;
                    monitor.CopilotToolCallContextLines = 0;
                    AppLogger.LogInfo(
                        $"AgentSessionManager: attached copilot log for session '{session.SessionId}', pid={monitor.BackendProcessId}, path='{discoveredPath}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(monitor.CopilotLogPath))
            {
                return changed;
            }

            var offset = monitor.CopilotLogOffset;
            var toolCallContextLines = monitor.CopilotToolCallContextLines;
            if (!TryReadCopilotLogDelta(
                    monitor.CopilotLogPath,
                    ref offset,
                    ref toolCallContextLines,
                    out var nextState,
                    out var nextMessage))
            {
                return changed;
            }

            monitor.CopilotLogOffset = offset;
            monitor.CopilotToolCallContextLines = toolCallContextLines;

            if (nextState.HasValue && session.State != nextState.Value)
            {
                session.State = nextState.Value;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(nextMessage)
                && !string.Equals(session.StateMessage, nextMessage, StringComparison.Ordinal))
            {
                session.StateMessage = nextMessage;
                changed = true;
            }

            return changed;
        }

        private static int TryDiscoverBackendProcessId(
            AgentSessionRecord session,
            DirectSessionMonitorState monitor)
        {
            if (session == null || monitor == null || session.WtProcessId <= 0)
            {
                return 0;
            }

            var candidates = new List<string>();
            var configured = (monitor.BackendProcessName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }

            if (monitor.IsCopilot)
            {
                var copilotPid = ProcessTreeHelper.FindDescendantProcessIdByName(session.WtProcessId, "copilot");
                if (copilotPid > 0)
                {
                    return copilotPid;
                }

                if (!string.IsNullOrWhiteSpace(configured)
                    && !string.Equals(configured, "copilot", StringComparison.OrdinalIgnoreCase))
                {
                    var configuredPid = ProcessTreeHelper.FindDescendantProcessIdByName(session.WtProcessId, configured);
                    if (configuredPid > 0)
                    {
                        return configuredPid;
                    }
                }

                var nodePid = ProcessTreeHelper.FindDescendantProcessIdByName(session.WtProcessId, "node", "nodejs");
                if (nodePid > 0)
                {
                    return nodePid;
                }
            }

            if (candidates.Count == 0)
            {
                return 0;
            }

            var distinct = candidates
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinct.Length == 0)
            {
                return 0;
            }

            return ProcessTreeHelper.FindDescendantProcessIdByName(session.WtProcessId, distinct);
        }

        private static bool TryReadCopilotLogDelta(
            string logPath,
            ref long offset,
            ref int toolCallContextLines,
            out AgentSessionState? state,
            out string message)
        {
            state = null;
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (offset < 0 || offset > stream.Length)
                {
                    offset = 0;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    if (TryParseCopilotLogStatus(line, ref toolCallContextLines, out var parsedState, out var parsedMessage))
                    {
                        state = parsedState;
                        message = parsedMessage;
                    }
                }

                offset = stream.Position;
                return state.HasValue;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseCopilotLogStatus(
            string line,
            ref int toolCallContextLines,
            out AgentSessionState state,
            out string message)
        {
            state = AgentSessionState.Running;
            message = string.Empty;
            var text = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.Contains("\"tool_calls\"", StringComparison.OrdinalIgnoreCase))
            {
                toolCallContextLines = Math.Max(toolCallContextLines, 24);
            }
            else if (toolCallContextLines > 0)
            {
                toolCallContextLines--;
            }

            if (text.Contains("\"kind\": \"assistant_turn_start\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"event\": \"assistant.turn_start\"", StringComparison.OrdinalIgnoreCase))
            {
                state = AgentSessionState.Running;
                message = "Copilot is processing...";
                return true;
            }

            if (text.Contains("Tool calls count:", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Running tool calls in parallel", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"has_tool_requests\": \"true\"", StringComparison.OrdinalIgnoreCase))
            {
                state = AgentSessionState.WaitingUser;
                message = "Copilot is waiting for tool approval/input in terminal.";
                return true;
            }

            if (text.Contains("[Telemetry] cli.tool_call", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"kind\": \"tool_call_executed\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"kind\": \"tool_call_result\"", StringComparison.OrdinalIgnoreCase))
            {
                state = AgentSessionState.Running;
                message = "Copilot is processing...";
                return true;
            }

            if (text.Contains("\"kind\": \"assistant_turn_end\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"event\": \"assistant.turn_end\"", StringComparison.OrdinalIgnoreCase))
            {
                state = AgentSessionState.WaitingUser;
                message = "Copilot is waiting for input in terminal.";
                return true;
            }

            if (text.Contains("\"tool_name\": \"ask_user\"", StringComparison.OrdinalIgnoreCase))
            {
                state = AgentSessionState.WaitingUser;
                message = "Copilot requested user input.";
                return true;
            }

            if (toolCallContextLines > 0
                && text.Contains("\"name\": \"ask_user\"", StringComparison.OrdinalIgnoreCase))
            {
                state = AgentSessionState.WaitingUser;
                message = "Copilot requested user input.";
                return true;
            }

            return false;
        }

        private static string FindCopilotLogPathByProcessId(int processId)
        {
            if (processId <= 0)
            {
                return string.Empty;
            }

            var logsDirectory = ResolveCopilotLogsDirectory();
            if (string.IsNullOrWhiteSpace(logsDirectory))
            {
                return string.Empty;
            }

            try
            {
                var pattern = $"process-*-{processId}.log";
                return Directory
                    .EnumerateFiles(logsDirectory, pattern, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FindCopilotLogPathByWorkingDirectory(
            string workingDirectory,
            DateTimeOffset sessionCreatedAt)
        {
            var normalizedWorkingDirectory = (workingDirectory ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkingDirectory))
            {
                return string.Empty;
            }

            var logsDirectory = ResolveCopilotLogsDirectory();
            if (string.IsNullOrWhiteSpace(logsDirectory))
            {
                return string.Empty;
            }

            var escapedWorkingDirectory = normalizedWorkingDirectory.Replace("\\", "\\\\", StringComparison.Ordinal);

            try
            {
                var candidates = Directory
                    .EnumerateFiles(logsDirectory, "process-*-*.log", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(file =>
                        file.Exists
                        && file.Length > 0
                        && file.LastWriteTimeUtc >= sessionCreatedAt.UtcDateTime.AddMinutes(-5))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(16);

                foreach (var file in candidates)
                {
                    if (LogFileContainsToken(file.FullName, normalizedWorkingDirectory)
                        || LogFileContainsToken(file.FullName, escapedWorkingDirectory))
                    {
                        return file.FullName;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool LogFileContainsToken(string path, string token)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(token) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytesToRead = (int)Math.Min(stream.Length, 512 * 1024);
                if (bytesToRead <= 0)
                {
                    return false;
                }

                var buffer = new byte[bytesToRead];
                var read = stream.Read(buffer, 0, bytesToRead);
                if (read <= 0)
                {
                    return false;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static int ParseCopilotProcessIdFromLogPath(string logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return 0;
            }

            var fileName = Path.GetFileNameWithoutExtension(logPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return 0;
            }

            var lastDash = fileName.LastIndexOf("-", StringComparison.Ordinal);
            if (lastDash < 0 || lastDash >= fileName.Length - 1)
            {
                return 0;
            }

            var pidText = fileName[(lastDash + 1)..];
            return int.TryParse(pidText, out var pid) ? pid : 0;
        }

        private static string ResolveCopilotLogsDirectory()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return string.Empty;
            }

            var path = Path.Combine(userProfile, ".copilot", "logs");
            return Directory.Exists(path) ? path : string.Empty;
        }

        private void OnPipeEventReceived(object sender, AgentHubEventEnvelope message)
        {
            if (_disposed || message == null || string.IsNullOrWhiteSpace(message.SessionId))
            {
                return;
            }

            var timestamp = message.Timestamp == default ? DateTimeOffset.UtcNow : message.Timestamp;
            AgentSessionRecord session;
            AgentSessionState previousState;

            lock (_gate)
            {
                if (!_sessions.TryGetValue(message.SessionId, out var existing))
                {
                    existing = new AgentSessionRecord
                    {
                        SessionId = message.SessionId,
                        CreatedAt = timestamp,
                        LastUpdatedAt = timestamp,
                        State = AgentSessionState.Starting,
                    };
                    _sessions[existing.SessionId] = existing;
                }

                previousState = existing.State;
                ApplyEvent(existing, message);
                existing.LastUpdatedAt = timestamp;
                session = existing.Clone();
            }

            RaiseSessionChanged(session, previousState);
        }

        private static void ApplyEvent(AgentSessionRecord session, AgentHubEventEnvelope message)
        {
            var type = (message.Type ?? string.Empty).Trim().ToLowerInvariant();
            var payload = message.Payload;

            switch (type)
            {
                case "session.created":
                    session.TemplateId = ReadString(payload, "templateId", session.TemplateId);
                    session.DisplayName = ReadString(payload, "displayName", session.DisplayName);
                    session.Backend = ReadString(payload, "backend", session.Backend);
                    session.WorkingDir = ReadString(payload, "workingDir", session.WorkingDir);
                    if (session.State == AgentSessionState.Starting)
                    {
                        session.StateMessage = "Agent created.";
                    }

                    break;

                case "session.bound":
                    session.WtProcessId = ReadInt(payload, "wtProcessId", session.WtProcessId);
                    session.WtWindowHwnd = ReadULong(payload, "wtWindowHwnd", session.WtWindowHwnd);
                    break;

                case "status.changed":
                    session.State = ParseState(ReadString(payload, "state", session.State.ToString()), session.State);
                    session.StateMessage = ReadString(payload, "message", session.StateMessage);
                    break;

                case "heartbeat":
                    session.LastHeartbeatAt = message.Timestamp == default ? DateTimeOffset.UtcNow : message.Timestamp;
                    session.BackendProcessId = ReadInt(payload, "backendProcessId", session.BackendProcessId);
                    var heartbeatState = ReadString(payload, "state", string.Empty);
                    if (!string.IsNullOrWhiteSpace(heartbeatState))
                    {
                        session.State = ParseState(heartbeatState, session.State);
                    }

                    break;

                case "process.exited":
                    session.ExitCode = ReadInt(payload, "exitCode", session.ExitCode ?? 0);
                    break;

                case "error.raised":
                    session.State = AgentSessionState.Error;
                    session.StateMessage = ReadString(payload, "message", session.StateMessage);
                    break;
            }
        }

        private async Task<(bool Success, string Message, string DisplayName, string Backend, string WorkingDirectory, string CommandLine, IReadOnlyList<string> WaitLiterals, IReadOnlyList<string> WaitRegex, IReadOnlyDictionary<string, string> EnvironmentVariables)> PrepareTemplateLaunchAsync(
            TemplateDefinition template,
            string initialInput,
            CancellationToken cancellationToken)
        {
            if (template == null)
            {
                return (false, "Template is null.", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            var agent = template.Agent;
            if (agent == null || !agent.Enabled)
            {
                return (false, $"Template '{template.Name}' does not enable agent launch.", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            var command = (agent.Command ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                command = (agent.Name ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                return (false, "Template agent command is empty.", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            var repoRoot = ResolveRepoRoot(template);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                return (false, $"Template '{template.Name}' requires default repo root for agent worktree creation.", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            if (!Directory.Exists(repoRoot))
            {
                return (false, $"Repo path '{repoRoot}' does not exist.", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            if (!IsGitRepository(repoRoot))
            {
                return (false, $"Repo path '{repoRoot}' is not a git repository.", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            var worktreeBaseBranch = string.IsNullOrWhiteSpace(template.Creation?.WorktreeBaseBranch)
                ? "main"
                : template.Creation.WorktreeBaseBranch.Trim();
            var worktreeHint = BuildAgentTaskSlug(initialInput);
            var worktreeResult = await CreateAgentWorktreeAsync(
                    repoRoot,
                    worktreeHint,
                    worktreeBaseBranch,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!worktreeResult.Success)
            {
                return (false, worktreeResult.Message, string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            AppLogger.LogInfo(
                $"AgentSessionManager: created worktree. template='{template.Name}', branch='{worktreeResult.BranchName}', path='{worktreeResult.WorktreePath}'.");

            var effectiveRepo = worktreeResult.WorktreePath;
            var workingDirectory = ApplyTokens(agent.WorkingDirectory, template, effectiveRepo);
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = effectiveRepo;
            }

            workingDirectory = Environment.ExpandEnvironmentVariables((workingDirectory ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = Environment.CurrentDirectory;
            }

            if (!Directory.Exists(workingDirectory))
            {
                return (false, $"Template working directory does not exist: {workingDirectory}", string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            }

            var displayBase = string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName;
            var displayName = $"{displayBase} {DateTime.Now:HHmmss}".Trim();
            var backend = string.IsNullOrWhiteSpace(agent.Name) ? InferBackend(command) : agent.Name.Trim().ToLowerInvariant();

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (agent.Environment != null)
            {
                foreach (var pair in agent.Environment)
                {
                    var key = (pair.Key ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    env[key] = ApplyTokens(pair.Value, template, effectiveRepo);
                }
            }

            var waitLiterals = (agent.WaitLiterals ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var waitRegex = (agent.WaitRegex ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preparedCommand = ApplyTokens(command, template, effectiveRepo);
            return (true, string.Empty, displayName, backend, workingDirectory, preparedCommand, waitLiterals, waitRegex, env);
        }

        private (bool Success, string Message, int WtProcessId) LaunchAgentInWindowsTerminal(
            AgentSessionRecord session,
            (bool Success, string Message, string DisplayName, string Backend, string WorkingDirectory, string CommandLine, IReadOnlyList<string> WaitLiterals, IReadOnlyList<string> WaitRegex, IReadOnlyDictionary<string, string> EnvironmentVariables) prepared,
            string initialInput)
        {
            var terminal = ExecutableLocator.Resolve("terminal");
            if (!terminal.Exists || string.IsNullOrWhiteSpace(terminal.Resolved))
            {
                return (false, "Windows Terminal (wt.exe) was not found.", 0);
            }

            var normalizedInitialInput = NormalizeInitialInput(initialInput);
            if (!TryBuildLaunchCommand(
                    prepared.CommandLine,
                    prepared.Backend,
                    normalizedInitialInput,
                    out var executable,
                    out var arguments,
                    out var buildError))
            {
                return (false, buildError, 0);
            }

            var title = BuildWindowTitle(session, prepared.DisplayName);
            try
            {
                var startInfo = BuildTerminalStartInfo(
                    terminal.Resolved,
                    prepared.WorkingDirectory,
                    title,
                    executable,
                    arguments,
                    prepared.EnvironmentVariables);
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return (false, "Failed to start Windows Terminal.", 0);
                }

                AppLogger.LogInfo(
                    $"AgentSessionManager: launched wt direct backend. pid={process.Id}, template='{session.TemplateId}', session='{session.SessionId}', cwd='{startInfo.WorkingDirectory}', backend='{prepared.Backend}', hasInitialInput={!string.IsNullOrWhiteSpace(normalizedInitialInput)}.");
                return (true, string.Empty, process.Id);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("AgentSessionManager: terminal launch failed.", ex);
                return (false, $"Failed to launch Windows Terminal: {ex.Message}", 0);
            }
        }

        private static ProcessStartInfo BuildTerminalStartInfo(
            string terminalPath,
            string startingDirectory,
            string title,
            string commandExecutable,
            IReadOnlyList<string> commandArguments,
            IReadOnlyDictionary<string, string> environmentVariables)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = terminalPath,
                UseShellExecute = false,
                WorkingDirectory = startingDirectory,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add("new");
            startInfo.ArgumentList.Add("new-tab");
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add(title);
            startInfo.ArgumentList.Add("--suppressApplicationTitle");
            startInfo.ArgumentList.Add("--startingDirectory");
            startInfo.ArgumentList.Add(startingDirectory);
            startInfo.ArgumentList.Add(commandExecutable);
            foreach (var argument in commandArguments ?? Array.Empty<string>())
            {
                startInfo.ArgumentList.Add(argument ?? string.Empty);
            }

            ApplyEnvironmentOverrides(startInfo, environmentVariables);

            return startInfo;
        }

        private void RegisterDirectMonitor(
            AgentSessionRecord session,
            (bool Success, string Message, string DisplayName, string Backend, string WorkingDirectory, string CommandLine, IReadOnlyList<string> WaitLiterals, IReadOnlyList<string> WaitRegex, IReadOnlyDictionary<string, string> EnvironmentVariables) prepared)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionId))
            {
                return;
            }

            var state = new DirectSessionMonitorState
            {
                IsCopilot = IsCopilotCommand(prepared.CommandLine, prepared.Backend),
                BackendProcessName = ResolveBackendProcessName(prepared.CommandLine, prepared.Backend),
                BackendProcessId = 0,
                CopilotLogPath = string.Empty,
                CopilotLogOffset = 0,
            };

            lock (_gate)
            {
                _directMonitors[session.SessionId] = state;
            }
        }

        private void UnregisterDirectMonitor(string sessionId)
        {
            var key = (sessionId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_gate)
            {
                _directMonitors.Remove(key);
            }
        }

        private static bool TryBuildLaunchCommand(
            string commandLine,
            string backend,
            string initialInput,
            out string executable,
            out List<string> arguments,
            out string error)
        {
            executable = string.Empty;
            arguments = new List<string>();
            error = string.Empty;

            var parts = SplitCommandLine(commandLine);
            if (parts.Count == 0)
            {
                error = "Template agent command is empty.";
                return false;
            }

            executable = parts[0];
            if (string.IsNullOrWhiteSpace(executable))
            {
                error = "Template agent executable is empty.";
                return false;
            }

            var isCopilot = IsCopilotCommand(commandLine, backend);
            if (isCopilot)
            {
                var resolvedCopilot = ExecutableLocator.Resolve("copilot");
                if (resolvedCopilot.Exists && !string.IsNullOrWhiteSpace(resolvedCopilot.Resolved))
                {
                    executable = resolvedCopilot.Resolved;
                }
            }

            for (var i = 1; i < parts.Count; i++)
            {
                arguments.Add(parts[i]);
            }

            if (!string.IsNullOrWhiteSpace(initialInput)
                && isCopilot
                && !ContainsCopilotPromptSwitch(commandLine))
            {
                arguments.Add("-i");
                arguments.Add(initialInput);
            }

            return true;
        }

        private static List<string> SplitCommandLine(string commandLine)
        {
            var value = (commandLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            var argv = CommandLineToArgvW(value, out var argc);
            if (argv == IntPtr.Zero || argc <= 0)
            {
                return new List<string>();
            }

            try
            {
                var args = new List<string>(argc);
                for (var i = 0; i < argc; i++)
                {
                    var ptr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    var item = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                    args.Add(item);
                }

                return args;
            }
            finally
            {
                _ = LocalFree(argv);
            }
        }

        private static string ResolveBackendProcessName(string commandLine, string backend)
        {
            var backendToken = (backend ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(backendToken)
                && !string.Equals(backendToken, "custom", StringComparison.OrdinalIgnoreCase))
            {
                var fromBackend = Path.GetFileNameWithoutExtension(backendToken.Trim('"', '\''));
                if (!string.IsNullOrWhiteSpace(fromBackend))
                {
                    return fromBackend.Trim();
                }
            }

            var parts = SplitCommandLine(commandLine);
            if (parts.Count > 0)
            {
                var fromCommand = Path.GetFileNameWithoutExtension((parts[0] ?? string.Empty).Trim('"', '\''));
                if (!string.IsNullOrWhiteSpace(fromCommand))
                {
                    return fromCommand.Trim();
                }
            }

            return string.Empty;
        }

        private static bool IsCopilotCommand(string commandLine, string backend)
        {
            var backendToken = (backend ?? string.Empty).Trim();
            if (string.Equals(backendToken, "copilot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var executable = ResolveBackendProcessName(commandLine, string.Empty);
            return string.Equals(executable, "copilot", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsCopilotPromptSwitch(string commandLine)
        {
            var parts = SplitCommandLine(commandLine);
            for (var i = 1; i < parts.Count; i++)
            {
                var arg = (parts[i] ?? string.Empty).Trim();
                if (arg.Equals("-i", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("--interactive", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("-p", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("--prompt", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ApplyEnvironmentOverrides(
            ProcessStartInfo startInfo,
            IReadOnlyDictionary<string, string> env)
        {
            if (startInfo == null || env == null || env.Count == 0)
            {
                return;
            }

            foreach (var pair in env)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                startInfo.Environment[key] = pair.Value ?? string.Empty;
            }
        }

        private void UpsertSession(AgentSessionRecord session, AgentSessionState previousState)
        {
            AgentSessionRecord snapshot;
            lock (_gate)
            {
                _sessions[session.SessionId] = session.Clone();
                snapshot = _sessions[session.SessionId].Clone();
            }

            RaiseSessionChanged(snapshot, previousState);
        }

        private bool TryGetSession(string sessionId, out AgentSessionRecord session)
        {
            session = null;
            var key = (sessionId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(key, out var existing))
                {
                    return false;
                }

                session = existing.Clone();
                return true;
            }
        }

        private void RaiseSessionChanged(AgentSessionRecord session, AgentSessionState previousState)
        {
            try
            {
                SessionChanged?.Invoke(this, new AgentSessionChangedEventArgs
                {
                    Session = session?.Clone(),
                    PreviousState = previousState,
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"AgentSessionManager: session changed callback failed ({ex.Message}).");
            }
        }

        private static bool TryKillProcess(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }

                process.Kill(entireProcessTree: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTerminalClosureGraceElapsed(AgentSessionRecord session, DateTimeOffset now)
        {
            if (session == null)
            {
                return true;
            }

            return now - session.CreatedAt >= WindowClosedGracePeriod
                && now - session.LastUpdatedAt >= WindowClosedGracePeriod;
        }

        private static bool TryRebindTerminalWindow(
            AgentSessionRecord session,
            DateTimeOffset now,
            out bool changed)
        {
            changed = false;
            if (session == null)
            {
                return false;
            }

            var hwnd = ResolveSessionWindowHandle(session, out _);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var resolvedHwnd = unchecked((ulong)hwnd.ToInt64());
            if (session.WtWindowHwnd != resolvedHwnd)
            {
                session.WtWindowHwnd = resolvedHwnd;
                changed = true;
            }

            if (NativeWindowHelper.TryCreateWindowInfo(hwnd, out var info)
                && info?.ProcessId > 0
                && session.WtProcessId != (int)info.ProcessId)
            {
                var previousPid = session.WtProcessId;
                session.WtProcessId = (int)info.ProcessId;
                changed = true;
                AppLogger.LogInfo(
                    $"AgentSessionManager: rebound terminal host pid for session '{session.SessionId}' from {previousPid} to {session.WtProcessId}.");
            }

            if (changed)
            {
                session.LastUpdatedAt = now;
            }

            return true;
        }

        private static IntPtr ResolveSessionWindowHandle(AgentSessionRecord session, out string reason)
        {
            reason = string.Empty;
            if (session == null)
            {
                reason = "Session is null.";
                return IntPtr.Zero;
            }

            if (TryResolveWindowHandleByTitle(session, out var byTitleFirst, out var byTitleReasonFirst))
            {
                reason = byTitleReasonFirst;
                return byTitleFirst;
            }

            if (session.WtWindowHwnd != 0)
            {
                var known = new IntPtr(unchecked((long)session.WtWindowHwnd));
                if (NativeWindowHelper.IsWindowHandleValid(known))
                {
                    return known;
                }
            }

            if (session.WtProcessId > 0)
            {
                var windows = NativeWindowHelper.EnumerateProcessWindows(session.WtProcessId);
                if (windows != null && windows.Count > 0)
                {
                    foreach (var hwnd in windows)
                    {
                        if (hwnd == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (NativeWindowHelper.IsWindowCloaked(hwnd))
                        {
                            continue;
                        }

                        return hwnd;
                    }

                    return windows[0];
                }
            }

            if (TryResolveWindowHandleByTitle(session, out var byTitle, out var byTitleReason))
            {
                reason = byTitleReason;
                return byTitle;
            }

            reason = string.IsNullOrWhiteSpace(byTitleReason)
                ? "No matching window was found by handle, process id, or title."
                : byTitleReason;
            return IntPtr.Zero;
        }

        private static bool TryResolveWindowHandleByTitle(
            AgentSessionRecord session,
            out IntPtr hwnd,
            out string reason)
        {
            hwnd = IntPtr.Zero;
            reason = string.Empty;
            if (session == null || string.IsNullOrWhiteSpace(session.SessionId))
            {
                reason = "Session id is missing.";
                return false;
            }

            var shortId = session.SessionId.Substring(0, Math.Min(8, session.SessionId.Length));
            var titleToken = $"• {shortId}";
            var windows = NativeWindowHelper.EnumerateTopLevelWindows();
            if (windows == null || windows.Count == 0)
            {
                reason = "No top-level windows are available.";
                return false;
            }

            IntPtr best = IntPtr.Zero;
            var bestScore = int.MinValue;
            foreach (var candidate in windows)
            {
                if (candidate == IntPtr.Zero || NativeWindowHelper.IsWindowCloaked(candidate))
                {
                    continue;
                }

                if (!NativeWindowHelper.TryCreateWindowInfo(candidate, out var info) || info == null)
                {
                    continue;
                }

                var title = info.Title ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var score = 0;
                if (title.IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf(shortId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 8;
                }

                if (title.StartsWith("[PTA]", StringComparison.OrdinalIgnoreCase))
                {
                    score += 4;
                }

                if (!string.IsNullOrWhiteSpace(session.DisplayName)
                    && title.IndexOf(session.DisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 2;
                }

                if (string.Equals(info.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.ProcessName, "wt", StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                }

                if (score <= 0)
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == IntPtr.Zero)
            {
                reason = $"No window title match for token '{shortId}'.";
                return false;
            }

            hwnd = best;
            reason = $"Resolved by title token '{shortId}' (score={bestScore}).";
            return true;
        }

        private static string BuildWindowTitle(AgentSessionRecord session, string displayName)
        {
            var shortId = string.IsNullOrWhiteSpace(session?.SessionId)
                ? "session"
                : session.SessionId.Substring(0, Math.Min(8, session.SessionId.Length));
            var label = string.IsNullOrWhiteSpace(displayName) ? "agent" : displayName.Trim();
            return $"[PTA] {label} • {shortId}";
        }

        private static string ResolveRepoRoot(TemplateDefinition template)
        {
            var value = (template?.DefaultRepoRoot ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var expanded = Environment.ExpandEnvironmentVariables(value);
            return string.IsNullOrWhiteSpace(expanded) ? string.Empty : expanded.Trim();
        }

        private static string ApplyTokens(string value, TemplateDefinition template, string repoRoot)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var workspaceTitle = string.IsNullOrWhiteSpace(template?.DisplayName)
                ? template?.Name ?? string.Empty
                : template.DisplayName;
            return text
                .Replace("{repo}", repoRoot ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{workspaceTitle}", workspaceTitle, StringComparison.OrdinalIgnoreCase)
                .Replace("{instanceName}", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeInitialInput(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Trim();
        }

        private static async Task<AgentWorktreeCreationResult> CreateAgentWorktreeAsync(
            string repoRoot,
            string taskHint,
            string baseBranch,
            CancellationToken cancellationToken)
        {
            var normalizedRepo = (repoRoot ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedRepo))
            {
                return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, "Repository path is required for worktree creation.");
            }

            if (!Directory.Exists(normalizedRepo))
            {
                return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, $"Repo path '{normalizedRepo}' does not exist.");
            }

            if (!IsGitRepository(normalizedRepo))
            {
                return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, $"Repo path '{normalizedRepo}' is not a git repository.");
            }

            var effectiveBaseBranch = string.IsNullOrWhiteSpace(baseBranch)
                ? "main"
                : baseBranch.Trim();
            var baseSlug = BuildWorktreeSlug(taskHint, "agent-task", 36);
            var worktreeRoot = Path.Combine(normalizedRepo, ".worktrees");
            Directory.CreateDirectory(worktreeRoot);

            for (var attempt = 0; attempt < 6; attempt++)
            {
                var branchName = $"agent-{baseSlug}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
                var worktreePath = Path.Combine(worktreeRoot, branchName);
                if (Directory.Exists(worktreePath))
                {
                    continue;
                }

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    startInfo.ArgumentList.Add("-C");
                    startInfo.ArgumentList.Add(normalizedRepo);
                    startInfo.ArgumentList.Add("worktree");
                    startInfo.ArgumentList.Add("add");
                    startInfo.ArgumentList.Add("-b");
                    startInfo.ArgumentList.Add(branchName);
                    startInfo.ArgumentList.Add(worktreePath);
                    startInfo.ArgumentList.Add(effectiveBaseBranch);

                    using var process = new Process { StartInfo = startInfo };
                    if (!process.Start())
                    {
                        return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, "Failed to start git process.");
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    var stdOut = (await stdOutTask.ConfigureAwait(false)).Trim();
                    var stdErr = (await stdErrTask.ConfigureAwait(false)).Trim();

                    if (process.ExitCode != 0)
                    {
                        var details = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                        if (string.IsNullOrWhiteSpace(details))
                        {
                            details = $"git exited with code {process.ExitCode}.";
                        }

                        TryDeleteDirectory(worktreePath);
                        return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, $"Failed to create git worktree: {details}");
                    }

                    if (!Directory.Exists(worktreePath))
                    {
                        return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, $"Worktree path '{worktreePath}' was not created.");
                    }

                    return new AgentWorktreeCreationResult(true, worktreePath, branchName, $"Created agent worktree '{branchName}'.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Win32Exception ex)
                {
                    return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, $"Failed to execute git: {ex.Message}");
                }
                catch (Exception ex)
                {
                    TryDeleteDirectory(worktreePath);
                    return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, $"Failed to create git worktree: {ex.Message}");
                }
            }

            return new AgentWorktreeCreationResult(false, string.Empty, string.Empty, "Failed to create a unique worktree name.");
        }

        private static bool IsGitRepository(string repoRoot)
        {
            var gitPath = Path.Combine(repoRoot, ".git");
            return Directory.Exists(gitPath) || File.Exists(gitPath);
        }

        private static string BuildAgentTaskSlug(string initialInput)
        {
            var text = NormalizeInitialInput(initialInput);
            if (string.IsNullOrWhiteSpace(text))
            {
                return "agent-task";
            }

            var marker = "/issues/";
            var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var start = markerIndex + marker.Length;
                var end = start;
                while (end < text.Length && text[end] >= '0' && text[end] <= '9')
                {
                    end++;
                }

                if (end > start)
                {
                    return $"issue-{text[start..end]}";
                }
            }

            return BuildWorktreeSlug(text, "agent-task", 36);
        }

        private static string BuildWorktreeSlug(string value, string fallback, int maxLength)
        {
            var source = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(source))
            {
                return fallback;
            }

            var builder = new StringBuilder(source.Length);
            var previousDash = false;
            foreach (var ch in source)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    if (builder.Length < maxLength)
                    {
                        builder.Append(ch);
                    }

                    previousDash = false;
                    continue;
                }

                if (!previousDash && builder.Length > 0 && builder.Length < maxLength)
                {
                    builder.Append('-');
                    previousDash = true;
                }
            }

            var slug = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static string InferBackend(string commandLine)
        {
            var value = (commandLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return "custom";
            }

            var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                return "custom";
            }

            token = token.Trim('"', '\'');
            var file = Path.GetFileNameWithoutExtension(token);
            return string.IsNullOrWhiteSpace(file) ? "custom" : file.ToLowerInvariant();
        }

        private static AgentSessionState ParseState(string value, AgentSessionState fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "starting" => AgentSessionState.Starting,
                "running" => AgentSessionState.Running,
                "waitinguser" => AgentSessionState.WaitingUser,
                "waiting-user" => AgentSessionState.WaitingUser,
                "waiting_user" => AgentSessionState.WaitingUser,
                "done" => AgentSessionState.Done,
                "error" => AgentSessionState.Error,
                "cancelled" => AgentSessionState.Cancelled,
                "canceled" => AgentSessionState.Cancelled,
                "ended" => AgentSessionState.Ended,
                _ => fallback,
            };
        }

        private static string ReadString(JsonElement payload, string property, string fallback)
        {
            if (payload.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(property))
            {
                return fallback ?? string.Empty;
            }

            if (!payload.TryGetProperty(property, out var item))
            {
                return fallback ?? string.Empty;
            }

            return item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : fallback ?? string.Empty;
        }

        private static int ReadInt(JsonElement payload, string property, int fallback)
        {
            if (payload.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(property))
            {
                return fallback;
            }

            if (!payload.TryGetProperty(property, out var item))
            {
                return fallback;
            }

            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value))
            {
                return value;
            }

            if (item.ValueKind == JsonValueKind.String
                && int.TryParse(item.GetString(), out value))
            {
                return value;
            }

            return fallback;
        }

        private static ulong ReadULong(JsonElement payload, string property, ulong fallback)
        {
            if (payload.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(property))
            {
                return fallback;
            }

            if (!payload.TryGetProperty(property, out var item))
            {
                return fallback;
            }

            if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out var value))
            {
                return value;
            }

            if (item.ValueKind == JsonValueKind.String
                && ulong.TryParse(item.GetString(), out value))
            {
                return value;
            }

            return fallback;
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
