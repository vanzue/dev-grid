// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using TopToolbar.Models;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel
    {
        private ToolbarDisplayMode _displayMode = ToolbarDisplayMode.TopBar;
        private bool _topBarEnabled = true;
        private bool _radialMenuEnabled = true;
        private bool _requireCtrlForTopBarTrigger;

        public ToolbarDisplayMode DisplayMode
        {
            get => _displayMode;
            set
            {
                if (_displayMode != value)
                {
                    SetProperty(ref _displayMode, value);
                    OnPropertyChanged(nameof(DisplayModeIndex));
                    OnPropertyChanged(nameof(IsTopBarModeSelected));
                    if (!_suppressGeneralSave)
                    {
                        ScheduleSave();
                    }
                }
            }
        }

        public int DisplayModeIndex
        {
            get => DisplayMode == ToolbarDisplayMode.RadialMenu ? 1 : 0;
            set => DisplayMode = value == 1 ? ToolbarDisplayMode.RadialMenu : ToolbarDisplayMode.TopBar;
        }

        public bool IsTopBarModeSelected => DisplayMode == ToolbarDisplayMode.TopBar;

        public bool TopBarEnabled
        {
            get => _topBarEnabled;
            set
            {
                if (_topBarEnabled != value)
                {
                    SetProperty(ref _topBarEnabled, value);
                    if (!_suppressGeneralSave)
                    {
                        ScheduleSave();
                    }
                }
            }
        }

        public bool RadialMenuEnabled
        {
            get => _radialMenuEnabled;
            set
            {
                if (_radialMenuEnabled != value)
                {
                    SetProperty(ref _radialMenuEnabled, value);
                    if (!_suppressGeneralSave)
                    {
                        ScheduleSave();
                    }
                }
            }
        }

        public bool RequireCtrlForTopBarTrigger
        {
            get => _requireCtrlForTopBarTrigger;
            set
            {
                if (_requireCtrlForTopBarTrigger != value)
                {
                    SetProperty(ref _requireCtrlForTopBarTrigger, value);
                    if (!_suppressGeneralSave)
                    {
                        ScheduleSave();
                    }
                }
            }
        }
    }
}
