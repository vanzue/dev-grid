// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Windowing;
using TopToolbar.Services.Workspaces;

namespace TopToolbar.Services.Agents
{
    internal sealed class AgentWrapRunner : IDisposable
    {
        private static readonly string[] DefaultWaitLiterals =
        {
            "(y/n)",
            "[y/N]",
            "Continue?",
            "Press Enter",
            "Select",
            "Choose",
            "Are you sure",
        };
        private static readonly TimeSpan CopilotLogDiscoveryTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CopilotLogPollInterval = TimeSpan.FromMilliseconds(250);

        private readonly AgentRunOptions _options;
        private readonly AgentHubPipeClient _pipeClient;
        private readonly List<Regex> _waitRegex;
        private readonly List<string> _waitLiterals;
        private readonly SemaphoreSlim _stateGate = new(1, 1);
        private Process _backendProcess;
        private AgentSessionState _state = AgentSessionState.Starting;
        private DateTimeOffset _lastOutputAt = DateTimeOffset.UtcNow;
        private DateTimeOffset _lastWaitingUserAt = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSilenceNoticeAt = DateTimeOffset.MinValue;
        private bool _backendUsesDirectConsoleIo;
        private bool _disposed;

        public AgentWrapRunner(AgentRunOptions options, AgentHubPipeClient pipeClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _pipeClient = pipeClient ?? new AgentHubPipeClient();
            _waitLiterals = BuildWaitLiterals(options.WaitLiterals);
            _waitRegex = BuildWaitRegex(options.WaitRegexPatterns);
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            var hasSession = !string.IsNullOrWhiteSpace(_options.SessionId);
            if (!hasSession)
            {
                Console.Error.WriteLine("Missing --session.");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(_options.CommandLine))
            {
                Console.Error.WriteLine("Missing --command/--command64.");
                return 2;
            }

            var workingDirectory = ResolveWorkingDirectory(_options.WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
            {
                Console.Error.WriteLine($"Working directory does not exist: {workingDirectory}");
                WriteSystemLine($"Working directory does not exist: {workingDirectory}", isError: true);
                await EmitErrorAsync("working-dir-missing", $"Working directory does not exist: {workingDirectory}", cancellationToken)
                    .ConfigureAwait(false);
                await SetStateAsync(AgentSessionState.Error, "Working directory does not exist.", cancellationToken).ConfigureAwait(false);
                return 2;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                linkedCts.Cancel();
                _ = OnUserCancelledAsync();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                var initialInput = NormalizeInitialInput(_options.InitialInput);
                var commandLine = (_options.CommandLine ?? string.Empty).Trim();
                var startupTaskHandledByCommand = ShouldLaunchCopilotWithInitialTask(
                    commandLine,
                    _options.Backend,
                    initialInput);
                var useDirectConsoleIo = ShouldUseDirectTerminalIo(
                    commandLine,
                    _options.Backend,
                    startupTaskHandledByCommand,
                    initialInput);
                _backendUsesDirectConsoleIo = useDirectConsoleIo;

                WriteSystemLine($"Session {_options.SessionId} starting.");
                WriteSystemLine($"Working directory: {workingDirectory}");
                if (startupTaskHandledByCommand)
                {
                    WriteSystemLine("Backend command: copilot -i <initial-task>");
                }
                else
                {
                    WriteSystemLine($"Backend command: {commandLine}");
                }

                if (!string.IsNullOrWhiteSpace(initialInput))
                {
                    WriteSystemLine($"Startup task received (length={initialInput.Length}).");
                }

                await EmitCreatedAsync(workingDirectory, linkedCts.Token).ConfigureAwait(false);
                await BindTerminalWindowAsync(linkedCts.Token).ConfigureAwait(false);

                var startInfo = startupTaskHandledByCommand
                    ? BuildDirectCopilotStartInfo(workingDirectory, initialInput, redirectIo: !useDirectConsoleIo)
                    : BuildShellCommandStartInfo(workingDirectory, commandLine, redirectIo: !useDirectConsoleIo);
                ApplyEnvironmentOverrides(startInfo, _options.EnvironmentVariables);

                _backendProcess = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true,
                };

                if (!_backendProcess.Start())
                {
                    WriteSystemLine("Backend process failed to start.", isError: true);
                    await EmitErrorAsync("backend-start-failed", "Backend process failed to start.", linkedCts.Token)
                        .ConfigureAwait(false);
                    await SetStateAsync(AgentSessionState.Error, "Backend process failed to start.", linkedCts.Token)
                        .ConfigureAwait(false);
                    return 1;
                }

                WriteSystemLine($"Backend process started (pid={_backendProcess.Id}).");
                await EmitBoundAsync(linkedCts.Token).ConfigureAwait(false);
                await SetStateAsync(AgentSessionState.Running, "Agent is running.", linkedCts.Token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(initialInput))
                {
                    if (startupTaskHandledByCommand)
                    {
                        WriteSystemLine("Startup task passed to Copilot via -i argument.");
                        AppLogger.LogInfo(
                            $"AgentWrapRunner: startup task passed via -i (length={initialInput.Length}). session='{_options.SessionId}'.");
                    }
                    else if (useDirectConsoleIo)
                    {
                        WriteSystemLine("Direct terminal mode is enabled. Type additional input directly in this terminal.");
                    }
                    else
                    {
                        var injected = await TryInjectInitialInputAsync(
                            _backendProcess.StandardInput,
                            initialInput).ConfigureAwait(false);
                        if (injected)
                        {
                            WriteSystemLine($"Startup task sent to agent (length={initialInput.Length}).");
                            AppLogger.LogInfo(
                                $"AgentWrapRunner: initial input injected (length={initialInput.Length}). session='{_options.SessionId}'.");
                        }
                        else
                        {
                            WriteSystemLine("Failed to send startup task to agent.", isError: true);
                        }
                    }
                }

                if (useDirectConsoleIo)
                {
                    WriteSystemLine("Backend is attached directly to terminal I/O.");
                }
                else
                {
                    WriteSystemLine("Streaming backend output below. Type in this terminal if agent asks for input.");
                }

                var heartbeatTask = SendHeartbeatLoopAsync(linkedCts.Token);
                Task copilotStatusTask = Task.CompletedTask;
                Task stdOutTask = Task.CompletedTask;
                Task stdErrTask = Task.CompletedTask;
                if (useDirectConsoleIo && IsCopilotCommand(commandLine, _options.Backend))
                {
                    copilotStatusTask = MonitorCopilotStatusFromLogAsync(_backendProcess.Id, linkedCts.Token);
                }

                if (!useDirectConsoleIo)
                {
                    stdOutTask = PumpOutputAsync(_backendProcess.StandardOutput, isError: false, linkedCts.Token);
                    stdErrTask = PumpOutputAsync(_backendProcess.StandardError, isError: true, linkedCts.Token);
                    _ = PumpInputAsync(_backendProcess.StandardInput, linkedCts.Token);
                }

                await _backendProcess.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);

                var code = _backendProcess.ExitCode;
                await EmitProcessExitedAsync(code, linkedCts.Token).ConfigureAwait(false);
                WriteSystemLine($"Backend exited with code {code}.");
                if (_state != AgentSessionState.Cancelled)
                {
                    var finalState = code == 0 ? AgentSessionState.Done : AgentSessionState.Error;
                    var message = code == 0
                        ? "Agent completed."
                        : $"Agent exited with code {code}.";
                    await SetStateAsync(finalState, message, linkedCts.Token).ConfigureAwait(false);
                }

                linkedCts.Cancel();
                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await copilotStatusTask.ConfigureAwait(false);
                }
                catch
                {
                }

                if (_options.HoldOpen)
                {
                    Console.WriteLine();
                    Console.WriteLine("Session finished. Press Enter to close this terminal.");
                    _ = Console.ReadLine();
                }

                return code;
            }
            catch (OperationCanceledException)
            {
                WriteSystemLine("Session cancelled.");
                await SetStateAsync(AgentSessionState.Cancelled, "Cancelled.", CancellationToken.None).ConfigureAwait(false);
                await EmitProcessExitedAsync(-1, CancellationToken.None).ConfigureAwait(false);
                return 130;
            }
            catch (Exception ex)
            {
                WriteSystemLine($"Unhandled error: {ex.Message}", isError: true);
                AppLogger.LogError("AgentWrapRunner: unhandled failure.", ex);
                await EmitErrorAsync("agentwrap-unhandled", ex.Message, CancellationToken.None).ConfigureAwait(false);
                await SetStateAsync(AgentSessionState.Error, ex.Message, CancellationToken.None).ConfigureAwait(false);
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                TryTerminateBackend();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pipeClient.Dispose();
            _stateGate.Dispose();
            GC.SuppressFinalize(this);
        }

        private static async Task<bool> TryInjectInitialInputAsync(
            StreamWriter writer,
            string initialInput)
        {
            if (writer == null || string.IsNullOrWhiteSpace(initialInput))
            {
                return false;
            }

            try
            {
                await writer.WriteLineAsync(initialInput).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"AgentWrapRunner: initial input injection failed ({ex.Message}).");
                return false;
            }
        }

        private async Task PumpOutputAsync(StreamReader reader, bool isError, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (line == null)
                {
                    break;
                }

                _lastOutputAt = DateTimeOffset.UtcNow;
                _lastSilenceNoticeAt = DateTimeOffset.MinValue;
                if (isError)
                {
                    await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
                }
                else
                {
                    await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
                }

                if (_state == AgentSessionState.WaitingUser)
                {
                    await SetStateAsync(AgentSessionState.Running, "Output resumed.", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_state != AgentSessionState.Running)
                {
                    continue;
                }

                if (IsLikelyWaitingForUser(line))
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastWaitingUserAt >= TimeSpan.FromSeconds(4))
                    {
                        _lastWaitingUserAt = now;
                        await SetStateAsync(
                            AgentSessionState.WaitingUser,
                            "Agent may be waiting for input.",
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task PumpInputAsync(StreamWriter writer, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await Console.In.ReadLineAsync().ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (line == null)
                {
                    break;
                }

                if (_state == AgentSessionState.WaitingUser)
                {
                    await SetStateAsync(AgentSessionState.Running, "User input provided.", cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task SendHeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await _pipeClient.SendAsync(
                    BuildMessage(
                        "heartbeat",
                        new
                        {
                            state = _state.ToString(),
                            lastOutputAt = _lastOutputAt,
                            backendProcessId = _backendProcess?.Id ?? 0,
                        }),
                    cancellationToken).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                if (!_backendUsesDirectConsoleIo && _state == AgentSessionState.Running)
                {
                    var silence = now - _lastOutputAt;
                    if (silence >= TimeSpan.FromSeconds(6)
                        && (_lastSilenceNoticeAt == DateTimeOffset.MinValue
                            || now - _lastSilenceNoticeAt >= TimeSpan.FromSeconds(9)))
                    {
                        _lastSilenceNoticeAt = now;
                        WriteSystemLine($"Still waiting for backend output ({(int)silence.TotalSeconds}s).");
                    }
                }
            }
        }

        private async Task MonitorCopilotStatusFromLogAsync(int backendProcessId, CancellationToken cancellationToken)
        {
            if (backendProcessId <= 0)
            {
                return;
            }

            var logsDirectory = ResolveCopilotLogsDirectory();
            if (string.IsNullOrWhiteSpace(logsDirectory))
            {
                return;
            }

            var logPath = await WaitForCopilotLogPathAsync(logsDirectory, backendProcessId, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(logPath))
            {
                AppLogger.LogInfo(
                    $"AgentWrapRunner: copilot log file not found for pid={backendProcessId}. session='{_options.SessionId}'.");
                return;
            }

            AppLogger.LogInfo(
                $"AgentWrapRunner: monitoring copilot log. session='{_options.SessionId}', pid={backendProcessId}, path='{logPath}'.");

            try
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                while (!cancellationToken.IsCancellationRequested)
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        await ApplyCopilotLogStatusAsync(line, cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(CopilotLogPollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning(
                    $"AgentWrapRunner: copilot log monitoring failed ({ex.GetType().Name}: {ex.Message}). session='{_options.SessionId}'.");
            }
        }

        private async Task ApplyCopilotLogStatusAsync(string line, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("\"kind\": \"assistant_turn_start\"", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"event\": \"assistant.turn_start\"", StringComparison.OrdinalIgnoreCase))
            {
                await SetStateAsync(
                        AgentSessionState.Running,
                        "Copilot is processing...",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (trimmed.StartsWith("\"kind\": \"assistant_turn_end\"", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("\"event\": \"assistant.turn_end\"", StringComparison.OrdinalIgnoreCase))
            {
                await SetStateAsync(
                        AgentSessionState.WaitingUser,
                        "Copilot is waiting for input in terminal.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
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

        private static async Task<string> WaitForCopilotLogPathAsync(
            string logsDirectory,
            int backendProcessId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(logsDirectory)
                || backendProcessId <= 0
                || !Directory.Exists(logsDirectory))
            {
                return string.Empty;
            }

            var pattern = $"process-*-{backendProcessId}.log";
            var deadline = DateTimeOffset.UtcNow + CopilotLogDiscoveryTimeout;
            while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var match = Directory
                        .EnumerateFiles(logsDirectory, pattern, SearchOption.TopDirectoryOnly)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        return match;
                    }
                }
                catch
                {
                }

                await Task.Delay(CopilotLogPollInterval, cancellationToken).ConfigureAwait(false);
            }

            return string.Empty;
        }

        private async Task EmitCreatedAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            await _pipeClient.SendAsync(
                BuildMessage(
                    "session.created",
                    new
                    {
                        templateId = _options.TemplateId ?? string.Empty,
                        backend = NormalizeBackend(_options.Backend, _options.CommandLine),
                        workingDir = workingDirectory ?? string.Empty,
                        displayName = _options.DisplayName ?? string.Empty,
                    }),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task EmitBoundAsync(CancellationToken cancellationToken)
        {
            var wtProcessId = ProcessTreeHelper.FindAncestorProcessIdByName(
                Process.GetCurrentProcess().Id,
                "WindowsTerminal",
                "wt");
            var hwnd = ResolveWindowHandleForSession(wtProcessId, _options.SessionId);

            await _pipeClient.SendAsync(
                BuildMessage(
                    "session.bound",
                    new
                    {
                        wtProcessId,
                        wtWindowHwnd = hwnd == IntPtr.Zero
                            ? 0UL
                            : (ulong)hwnd.ToInt64(),
                    }),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task BindTerminalWindowAsync(CancellationToken cancellationToken)
        {
            await EmitBoundAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            await EmitBoundAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EmitProcessExitedAsync(int exitCode, CancellationToken cancellationToken)
        {
            await _pipeClient.SendAsync(
                BuildMessage(
                    "process.exited",
                    new
                    {
                        exitCode,
                    }),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task EmitErrorAsync(string errorCode, string message, CancellationToken cancellationToken)
        {
            await _pipeClient.SendAsync(
                BuildMessage(
                    "error.raised",
                    new
                    {
                        errorCode = (errorCode ?? string.Empty).Trim(),
                        message = (message ?? string.Empty).Trim(),
                    }),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task SetStateAsync(
            AgentSessionState state,
            string message,
            CancellationToken cancellationToken)
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_state == state)
                {
                    return;
                }

                _state = state;
            }
            finally
            {
                _stateGate.Release();
            }

            await _pipeClient.SendAsync(
                BuildMessage(
                    "status.changed",
                    new
                    {
                        state = state.ToString(),
                        message = (message ?? string.Empty).Trim(),
                    }),
                cancellationToken).ConfigureAwait(false);

            var messageText = (message ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(messageText))
            {
                WriteSystemLine($"State changed: {state}.");
            }
            else
            {
                WriteSystemLine($"State changed: {state} ({messageText}).");
            }
        }

        private AgentHubEventEnvelope BuildMessage(string type, object payload)
        {
            return new AgentHubEventEnvelope
            {
                Type = type ?? string.Empty,
                SessionId = _options.SessionId ?? string.Empty,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(payload),
            };
        }

        private static string ResolveWorkingDirectory(string value)
        {
            var expanded = Environment.ExpandEnvironmentVariables((value ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return Environment.CurrentDirectory;
            }

            return expanded;
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

        private static bool ShouldLaunchCopilotWithInitialTask(
            string commandLine,
            string backend,
            string initialInput)
        {
            var text = (commandLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(initialInput))
            {
                return false;
            }

            if (!IsCopilotCommand(text, backend))
            {
                return false;
            }

            if (ContainsCopilotPromptSwitch(text))
            {
                return false;
            }

            return text.Equals("copilot", StringComparison.OrdinalIgnoreCase)
                || text.Equals("copilot.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldUseDirectTerminalIo(
            string commandLine,
            string backend,
            bool startupTaskHandledByCommand,
            string initialInput)
        {
            if (!IsCopilotCommand(commandLine, backend))
            {
                return false;
            }

            if (startupTaskHandledByCommand)
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(initialInput);
        }

        private static ProcessStartInfo BuildShellCommandStartInfo(
            string workingDirectory,
            string commandLine,
            bool redirectIo)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput = redirectIo,
                RedirectStandardOutput = redirectIo,
                RedirectStandardError = redirectIo,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add((commandLine ?? string.Empty).Trim());
            return startInfo;
        }

        private static ProcessStartInfo BuildDirectCopilotStartInfo(
            string workingDirectory,
            string initialInput,
            bool redirectIo)
        {
            var resolved = ExecutableLocator.Resolve("copilot");
            var fileName = resolved.Exists && !string.IsNullOrWhiteSpace(resolved.Resolved)
                ? resolved.Resolved
                : "copilot";
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = redirectIo,
                RedirectStandardOutput = redirectIo,
                RedirectStandardError = redirectIo,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory,
            };
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add((initialInput ?? string.Empty).Trim());
            return startInfo;
        }

        private static bool IsCopilotCommand(string commandLine, string backend)
        {
            var normalizedBackend = (backend ?? string.Empty).Trim();
            if (string.Equals(normalizedBackend, "copilot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var text = (commandLine ?? string.Empty).Trim();
            return text.Equals("copilot", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("copilot ", StringComparison.OrdinalIgnoreCase)
                || text.Equals("copilot.exe", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("copilot.exe ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsCopilotPromptSwitch(string commandLine)
        {
            var text = commandLine ?? string.Empty;
            return Regex.IsMatch(
                text,
                @"(^|\s)(-i|--interactive|-p|--prompt)(\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private async Task OnUserCancelledAsync()
        {
            WriteSystemLine("Ctrl+C received. Stopping session.");
            await SetStateAsync(AgentSessionState.Cancelled, "Cancelled by user.", CancellationToken.None).ConfigureAwait(false);
            TryTerminateBackend();
        }

        private void TryTerminateBackend()
        {
            try
            {
                if (_backendProcess != null && !_backendProcess.HasExited)
                {
                    _backendProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        private bool IsLikelyWaitingForUser(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            foreach (var literal in _waitLiterals)
            {
                if (line.IndexOf(literal, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            foreach (var regex in _waitRegex)
            {
                if (regex.IsMatch(line))
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

        private static List<string> BuildWaitLiterals(IReadOnlyList<string> requested)
        {
            var values = new List<string>();
            foreach (var literal in DefaultWaitLiterals.Concat(requested ?? Array.Empty<string>()))
            {
                var candidate = (literal ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (!values.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(candidate);
                }
            }

            return values;
        }

        private static List<Regex> BuildWaitRegex(IReadOnlyList<string> requested)
        {
            var values = new List<Regex>();
            if (requested == null || requested.Count == 0)
            {
                return values;
            }

            foreach (var raw in requested)
            {
                var pattern = (raw ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                try
                {
                    values.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"AgentWrapRunner: invalid wait regex '{pattern}' ignored ({ex.Message}).");
                }
            }

            return values;
        }

        private static IntPtr ResolveWindowHandleForSession(int processId, string sessionId)
        {
            if (processId <= 0)
            {
                return IntPtr.Zero;
            }

            var shortId = string.IsNullOrWhiteSpace(sessionId)
                ? string.Empty
                : sessionId.Substring(0, Math.Min(8, sessionId.Length));
            if (!string.IsNullOrWhiteSpace(shortId))
            {
                var topLevel = NativeWindowHelper.EnumerateTopLevelWindows();
                if (topLevel != null && topLevel.Count > 0)
                {
                    foreach (var hwnd in topLevel)
                    {
                        if (hwnd == IntPtr.Zero || NativeWindowHelper.IsWindowCloaked(hwnd))
                        {
                            continue;
                        }

                        if (!NativeWindowHelper.TryCreateWindowInfo(hwnd, out var info) || info == null)
                        {
                            continue;
                        }

                        if ((int)info.ProcessId != processId)
                        {
                            continue;
                        }

                        var title = info.Title ?? string.Empty;
                        var hasShortId = title.IndexOf(shortId, StringComparison.OrdinalIgnoreCase) >= 0;
                        var hasPtaPrefix = title.IndexOf("[PTA]", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (hasShortId || (string.IsNullOrWhiteSpace(shortId) && hasPtaPrefix))
                        {
                            return hwnd;
                        }
                    }
                }
            }

            var windows = NativeWindowHelper.EnumerateProcessWindows(processId);
            if (windows == null || windows.Count == 0)
            {
                return IntPtr.Zero;
            }

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

        private static string NormalizeBackend(string backend, string commandLine)
        {
            var value = (backend ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.ToLowerInvariant();
            }

            var command = (commandLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                return "custom";
            }

            var first = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(first))
            {
                return "custom";
            }

            first = first.Trim('"', '\'');
            return first.ToLowerInvariant();
        }

        private static void WriteSystemLine(string message, bool isError = false)
        {
            var text = $"[Dev Grid Agent {DateTime.Now:HH:mm:ss}] {(message ?? string.Empty).Trim()}";
            if (isError)
            {
                Console.Error.WriteLine(text);
            }
            else
            {
                Console.WriteLine(text);
            }
        }

    }
}
