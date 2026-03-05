// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Services.Workspaces
{
    internal static class WorkspaceRuntimeSession
    {
        internal static string SessionId { get; } = Guid.NewGuid().ToString("N");

        internal static bool IsCurrentSession(string sessionId)
        {
            return !string.IsNullOrWhiteSpace(sessionId)
                && string.Equals(sessionId.Trim(), SessionId, StringComparison.OrdinalIgnoreCase);
        }
    }
}

