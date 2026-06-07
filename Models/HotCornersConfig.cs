// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace TopToolbar.Models;

public enum HotCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public static class HotCornerActions
{
    public const string None = "none";
    public const string Snapshot = "workspace.snapshot";
    public const string ShowDesktop = "system.showDesktop";
    public const string TaskView = "system.taskView";
    public const string LockScreen = "system.lockScreen";
    public const string StartScreenSaver = "system.startScreenSaver";
    public const string TurnOffDisplay = "system.turnOffDisplay";
}

public class HotCornersConfig
{
    public bool Enabled { get; set; }

    public int DwellMilliseconds { get; set; } = 250;

    public int HotZonePx { get; set; } = 6;

    public bool ShowCornerHints { get; set; } = true;

    public bool DisableOnFullScreen { get; set; } = true;

    public Dictionary<HotCorner, string> Actions { get; set; } = new();
}
