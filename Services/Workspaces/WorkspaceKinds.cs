// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Services.Workspaces
{
    internal static class WorkspaceKinds
    {
        internal const string Hot = "hot";
        internal const string Cold = "cold";

        internal static string Normalize(string kind, bool runtimeSessionOnly)
        {
            if (string.Equals(kind?.Trim(), Cold, StringComparison.OrdinalIgnoreCase))
            {
                return Cold;
            }

            if (string.Equals(kind?.Trim(), Hot, StringComparison.OrdinalIgnoreCase))
            {
                return Hot;
            }

            return runtimeSessionOnly ? Hot : Cold;
        }

        internal static bool IsHot(WorkspaceDefinition workspace)
        {
            if (workspace == null)
            {
                return false;
            }

            return string.Equals(
                Normalize(workspace.WorkspaceKind, workspace.RuntimeSessionOnly),
                Hot,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCold(WorkspaceDefinition workspace)
        {
            if (workspace == null)
            {
                return false;
            }

            return string.Equals(
                Normalize(workspace.WorkspaceKind, workspace.RuntimeSessionOnly),
                Cold,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
