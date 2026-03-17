// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;

namespace TopToolbar.Services.Agents
{
    internal sealed class AgentHubPipeClient : IDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private bool _disposed;

        public AgentHubPipeClient(string pipeName = AgentHubProtocol.PipeName)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? AgentHubProtocol.PipeName : pipeName.Trim();
        }

        public async Task SendAsync(AgentHubEventEnvelope message, CancellationToken cancellationToken)
        {
            if (message == null || _disposed)
            {
                return;
            }

            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                var line = JsonSerializer.Serialize(message);
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"AgentHubPipeClient: write failed ({ex.GetType().Name}: {ex.Message}).");
                ResetConnection();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResetConnection();
            GC.SuppressFinalize(this);
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_pipe is { IsConnected: true } && _writer != null)
            {
                return true;
            }

            ResetConnection();

            try
            {
                _pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
                await _pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

                _writer = new StreamWriter(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                return true;
            }
            catch (OperationCanceledException)
            {
                ResetConnection();
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.LogInfo($"AgentHubPipeClient: connect skipped ({ex.GetType().Name}: {ex.Message}).");
                ResetConnection();
                return false;
            }
        }

        private void ResetConnection()
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
            }

            _writer = null;

            try
            {
                _pipe?.Dispose();
            }
            catch
            {
            }

            _pipe = null;
        }
    }
}
