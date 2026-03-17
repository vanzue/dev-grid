// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;

namespace TopToolbar.Services.Agents
{
    internal sealed class AgentHubPipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly List<Task> _clientTasks = new();
        private readonly object _gate = new();
        private CancellationTokenSource _lifetime;
        private Task _acceptLoop;
        private bool _disposed;

        public event EventHandler<AgentHubEventEnvelope> EventReceived;

        public AgentHubPipeServer(string pipeName = AgentHubProtocol.PipeName)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? AgentHubProtocol.PipeName : pipeName.Trim();
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (_acceptLoop != null && !_acceptLoop.IsCompleted)
                {
                    return;
                }

                _lifetime = new CancellationTokenSource();
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
            }
        }

        public async Task StopAsync()
        {
            Task accept;
            List<Task> clients;

            lock (_gate)
            {
                _lifetime?.Cancel();
                accept = _acceptLoop;
                clients = new List<Task>(_clientTasks);
                _clientTasks.Clear();
                _acceptLoop = null;
                _lifetime?.Dispose();
                _lifetime = null;
            }

            if (accept != null)
            {
                try
                {
                    await accept.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (clients.Count > 0)
            {
                try
                {
                    await Task.WhenAll(clients).ConfigureAwait(false);
                }
                catch
                {
                }
            }
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
                StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }

            GC.SuppressFinalize(this);
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var task = Task.Run(() => HandleClientAsync(server, cancellationToken), cancellationToken);
                    lock (_gate)
                    {
                        _clientTasks.Add(task);
                    }

                    _ = task.ContinueWith(
                        completed =>
                        {
                            lock (_gate)
                            {
                                _clientTasks.Remove(completed);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    AppLogger.LogWarning($"AgentHubPipeServer: accept failed ({ex.GetType().Name}: {ex.Message}).");
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            try
            {
                using (pipe)
                using (var reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        AgentHubEventEnvelope message = null;
                        try
                        {
                            message = JsonSerializer.Deserialize<AgentHubEventEnvelope>(line);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning(
                                $"AgentHubPipeServer: invalid event payload ({ex.GetType().Name}: {ex.Message}).");
                        }

                        if (message == null || string.IsNullOrWhiteSpace(message.SessionId))
                        {
                            continue;
                        }

                        try
                        {
                            EventReceived?.Invoke(this, message);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning($"AgentHubPipeServer: subscriber error ({ex.GetType().Name}: {ex.Message}).");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"AgentHubPipeServer: client failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }
}
