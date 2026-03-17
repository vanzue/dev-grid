// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Services.Agents
{
    internal sealed class AgentSessionRecord
    {
        public string SessionId { get; set; } = string.Empty;

        public string WorkspaceId { get; set; } = string.Empty;

        public string TemplateId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Backend { get; set; } = string.Empty;

        public string WorkingDir { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public AgentSessionState State { get; set; } = AgentSessionState.Starting;

        public string StateMessage { get; set; } = string.Empty;

        public int WtProcessId { get; set; }

        public ulong WtWindowHwnd { get; set; }

        public int BackendProcessId { get; set; }

        public DateTimeOffset LastHeartbeatAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public int? ExitCode { get; set; }

        public bool IsActive =>
            State == AgentSessionState.Starting
            || State == AgentSessionState.Running
            || State == AgentSessionState.WaitingUser;

        public AgentSessionRecord Clone()
        {
            return new AgentSessionRecord
            {
                SessionId = SessionId,
                WorkspaceId = WorkspaceId,
                TemplateId = TemplateId,
                DisplayName = DisplayName,
                Backend = Backend,
                WorkingDir = WorkingDir,
                CreatedAt = CreatedAt,
                State = State,
                StateMessage = StateMessage,
                WtProcessId = WtProcessId,
                WtWindowHwnd = WtWindowHwnd,
                BackendProcessId = BackendProcessId,
                LastHeartbeatAt = LastHeartbeatAt,
                LastUpdatedAt = LastUpdatedAt,
                ExitCode = ExitCode,
            };
        }
    }

    internal sealed class AgentSessionChangedEventArgs : EventArgs
    {
        public AgentSessionRecord Session { get; init; }

        public AgentSessionState PreviousState { get; init; }
    }
}
