// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace TopToolbar.Services.Agents
{
    internal enum AgentSessionState
    {
        Starting = 0,
        Running = 1,
        WaitingUser = 2,
        Done = 3,
        Error = 4,
        Cancelled = 5,
        Ended = 6,
    }
}
