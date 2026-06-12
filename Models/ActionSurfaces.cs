// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Models
{
    /// <summary>
    /// Surfaces an action can be pinned or assigned to. Actions are unified across the top bar,
    /// radial ring, and hot corners; this flag controls where a given action is shown by default.
    /// </summary>
    [Flags]
    public enum ActionSurfaces
    {
        None = 0,
        Bar = 1,
        Ring = 2,
        Corner = 4,
    }
}
