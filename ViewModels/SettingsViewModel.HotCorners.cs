// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using TopToolbar.Models;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel
    {
        private const double MinDwellMs = 100;
        private const double MaxDwellMs = 1500;

        private bool _hotCornersEnabled;
        private double _hotCornerDwellMilliseconds = 250;
        private bool _hotCornerDisableOnFullScreen = true;
        private int _hotCornerZonePx = 6;
        private int _topLeftActionIndex;
        private int _topRightActionIndex;
        private int _bottomLeftActionIndex;
        private int _bottomRightActionIndex = 1;

        public bool HotCornersEnabled
        {
            get => _hotCornersEnabled;
            set => SetHotCornerProperty(ref _hotCornersEnabled, value);
        }

        public double HotCornerDwellMilliseconds
        {
            get => _hotCornerDwellMilliseconds;
            set
            {
                var clamped = Math.Clamp(value, MinDwellMs, MaxDwellMs);
                SetHotCornerProperty(ref _hotCornerDwellMilliseconds, clamped);
            }
        }

        public bool HotCornerDisableOnFullScreen
        {
            get => _hotCornerDisableOnFullScreen;
            set => SetHotCornerProperty(ref _hotCornerDisableOnFullScreen, value);
        }

        public int TopLeftActionIndex
        {
            get => _topLeftActionIndex;
            set => SetHotCornerProperty(ref _topLeftActionIndex, value);
        }

        public int TopRightActionIndex
        {
            get => _topRightActionIndex;
            set => SetHotCornerProperty(ref _topRightActionIndex, value);
        }

        public int BottomLeftActionIndex
        {
            get => _bottomLeftActionIndex;
            set => SetHotCornerProperty(ref _bottomLeftActionIndex, value);
        }

        public int BottomRightActionIndex
        {
            get => _bottomRightActionIndex;
            set => SetHotCornerProperty(ref _bottomRightActionIndex, value);
        }

        private void SetHotCornerProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return;
            }

            SetProperty(ref field, value, propertyName);
            if (!_suppressGeneralSave)
            {
                ScheduleSave();
            }
        }

        private void LoadHotCorners(HotCornersConfig config)
        {
            config ??= new HotCornersConfig();
            config.Actions ??= new Dictionary<HotCorner, string>();

            HotCornersEnabled = config.Enabled;
            HotCornerDwellMilliseconds = config.DwellMilliseconds <= 0 ? 250 : config.DwellMilliseconds;
            HotCornerDisableOnFullScreen = config.DisableOnFullScreen;
            _hotCornerZonePx = config.HotZonePx <= 0 ? 6 : config.HotZonePx;

            TopLeftActionIndex = ActionIdToIndex(GetAction(config, HotCorner.TopLeft));
            TopRightActionIndex = ActionIdToIndex(GetAction(config, HotCorner.TopRight));
            BottomLeftActionIndex = ActionIdToIndex(GetAction(config, HotCorner.BottomLeft));
            BottomRightActionIndex = ActionIdToIndex(GetAction(config, HotCorner.BottomRight));
        }

        private HotCornersConfig BuildHotCornersConfig()
        {
            return new HotCornersConfig
            {
                Enabled = HotCornersEnabled,
                DwellMilliseconds = (int)Math.Round(HotCornerDwellMilliseconds),
                HotZonePx = _hotCornerZonePx,
                DisableOnFullScreen = HotCornerDisableOnFullScreen,
                Actions = new Dictionary<HotCorner, string>
                {
                    [HotCorner.TopLeft] = IndexToActionId(TopLeftActionIndex),
                    [HotCorner.TopRight] = IndexToActionId(TopRightActionIndex),
                    [HotCorner.BottomLeft] = IndexToActionId(BottomLeftActionIndex),
                    [HotCorner.BottomRight] = IndexToActionId(BottomRightActionIndex),
                },
            };
        }

        private static string GetAction(HotCornersConfig config, HotCorner corner)
        {
            return config.Actions.TryGetValue(corner, out var value) ? value : HotCornerActions.None;
        }

        // Index mapping: 0 = None, 1 = Snap workspace.
        private static int ActionIdToIndex(string actionId)
        {
            return string.Equals(actionId, HotCornerActions.Snapshot, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private static string IndexToActionId(int index)
        {
            return index == 1 ? HotCornerActions.Snapshot : HotCornerActions.None;
        }
    }
}
