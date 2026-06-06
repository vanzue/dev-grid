// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace TopToolbar.Models;

public class ToolbarConfig
{
    public List<ButtonGroup> Groups { get; set; } = new();

    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ToolbarDisplayMode DisplayMode { get; set; } = ToolbarDisplayMode.TopBar;

    public bool TopBarEnabled { get; set; } = true;

    public bool RadialMenuEnabled { get; set; } = true;

    public bool RequireCtrlForTopBarTrigger { get; set; }

    public ToolbarTheme Theme { get; set; } = ToolbarTheme.WarmFrosted;

    public DefaultActionsConfig DefaultActions { get; set; } = new();
}
