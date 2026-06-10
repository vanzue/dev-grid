// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Models
{
    /// <summary>
    /// Surfaces an action (toolbar button) can be pinned to. Actions are unified across the
    /// top bar and the radial ring; this flag controls where a given action is shown.
    /// </summary>
    [Flags]
    public enum ActionSurfaces
    {
        None = 0,
        Bar = 1,
        Ring = 2,
    }
}
