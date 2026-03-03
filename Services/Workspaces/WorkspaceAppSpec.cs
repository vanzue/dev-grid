// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace TopToolbar.Services.Workspaces
{
    internal readonly record struct WorkspaceAppSpec(
        string Role,
        string Exe,
        string Cwd,
        string Args,
        string Init,
        string Monitor);
}
