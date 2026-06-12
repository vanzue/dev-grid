// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Models;
using TopToolbar.Services;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel
    {
        private const double MinDwellMs = 100;
        private const double MaxDwellMs = 1500;

        private bool _hotCornersEnabled;
        private double _hotCornerDwellMilliseconds = 250;
        private bool _hotCornerDisableOnFullScreen;
        private bool _hotCornerShowHints = true;
        private int _hotCornerZonePx = 6;
        private string _topLeftActionId = HotCornerActions.None;
        private string _topRightActionId = HotCornerActions.None;
        private string _bottomLeftActionId = HotCornerActions.None;
        private string _bottomRightActionId = HotCornerActions.None;

        public ObservableCollection<HotCornerActionOption> HotCornerActionOptions { get; } = new();

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

        public bool HotCornerShowHints
        {
            get => _hotCornerShowHints;
            set => SetHotCornerProperty(ref _hotCornerShowHints, value);
        }

        public string TopLeftActionId
        {
            get => _topLeftActionId;
            set => SetHotCornerActionProperty(ref _topLeftActionId, value);
        }

        public string TopRightActionId
        {
            get => _topRightActionId;
            set => SetHotCornerActionProperty(ref _topRightActionId, value);
        }

        public string BottomLeftActionId
        {
            get => _bottomLeftActionId;
            set => SetHotCornerActionProperty(ref _bottomLeftActionId, value);
        }

        public string BottomRightActionId
        {
            get => _bottomRightActionId;
            set => SetHotCornerActionProperty(ref _bottomRightActionId, value);
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

        private void SetHotCornerActionProperty(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            var normalized = NormalizeActionId(value);
            SetHotCornerProperty(ref field, normalized, propertyName);
        }

        private void LoadHotCorners(HotCornersConfig config)
        {
            config ??= new HotCornersConfig();
            config.Actions ??= new Dictionary<HotCorner, string>();

            HotCornersEnabled = config.Enabled;
            HotCornerDwellMilliseconds = config.DwellMilliseconds <= 0 ? 250 : config.DwellMilliseconds;
            HotCornerDisableOnFullScreen = config.DisableOnFullScreen;
            HotCornerShowHints = config.ShowCornerHints;
            _hotCornerZonePx = config.HotZonePx <= 0 ? 6 : config.HotZonePx;

            TopLeftActionId = NormalizeActionId(GetAction(config, HotCorner.TopLeft));
            TopRightActionId = NormalizeActionId(GetAction(config, HotCorner.TopRight));
            BottomLeftActionId = NormalizeActionId(GetAction(config, HotCorner.BottomLeft));
            BottomRightActionId = NormalizeActionId(GetAction(config, HotCorner.BottomRight));
        }

        private HotCornersConfig BuildHotCornersConfig()
        {
            return new HotCornersConfig
            {
                Enabled = HotCornersEnabled,
                DwellMilliseconds = (int)Math.Round(HotCornerDwellMilliseconds),
                HotZonePx = _hotCornerZonePx,
                ShowCornerHints = HotCornerShowHints,
                DisableOnFullScreen = HotCornerDisableOnFullScreen,
                Actions = new Dictionary<HotCorner, string>
                {
                    [HotCorner.TopLeft] = NormalizeActionId(TopLeftActionId),
                    [HotCorner.TopRight] = NormalizeActionId(TopRightActionId),
                    [HotCorner.BottomLeft] = NormalizeActionId(BottomLeftActionId),
                    [HotCorner.BottomRight] = NormalizeActionId(BottomRightActionId),
                },
            };
        }

        private static string GetAction(HotCornersConfig config, HotCorner corner)
        {
            return config.Actions.TryGetValue(corner, out var value) ? value : HotCornerActions.None;
        }

        private async Task<IReadOnlyList<HotCornerActionOption>> BuildHotCornerActionOptionsAsync(CancellationToken cancellationToken)
        {
            var options = new List<HotCornerActionOption>
            {
                new(HotCornerActions.None, "None"),
            };

            if (_actionProviderService == null)
            {
                return options;
            }

            try
            {
                var providerIds = _actionProviderService.RegisteredProviderIds;
                var descriptors = await _actionProviderService
                    .DiscoverAsync(providerIds, new Actions.ActionContext(), cancellationToken)
                    .ConfigureAwait(false);

                foreach (var descriptor in descriptors
                    .Where(d => d != null && d.CanExecute != false)
                    .OrderBy(d => d.GroupHint ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Order ?? double.MaxValue)
                    .ThenBy(d => d.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    var key = ActionPinStore.GetProviderActionKey(descriptor.ProviderId, descriptor.Id);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    options.Add(new HotCornerActionOption(key, descriptor.Title));
                }
            }
            catch
            {
            }

            return options
                .GroupBy(option => option.ActionId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private void ReplaceHotCornerActionOptions(IReadOnlyList<HotCornerActionOption> options)
        {
            HotCornerActionOptions.Clear();
            foreach (var option in options ?? Array.Empty<HotCornerActionOption>())
            {
                HotCornerActionOptions.Add(option);
            }

            EnsureSelectedActionExists(ref _topLeftActionId, nameof(TopLeftActionId));
            EnsureSelectedActionExists(ref _topRightActionId, nameof(TopRightActionId));
            EnsureSelectedActionExists(ref _bottomLeftActionId, nameof(BottomLeftActionId));
            EnsureSelectedActionExists(ref _bottomRightActionId, nameof(BottomRightActionId));
        }

        private void EnsureSelectedActionExists(ref string field, string propertyName)
        {
            var normalized = NormalizeActionId(field);
            if (!HotCornerActionOptions.Any(option => string.Equals(option.ActionId, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                normalized = HotCornerActions.None;
            }

            if (!string.Equals(field, normalized, StringComparison.Ordinal))
            {
                field = normalized;
                OnPropertyChanged(propertyName);
            }
        }

        private static string NormalizeActionId(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId) ||
                string.Equals(actionId, HotCornerActions.None, StringComparison.OrdinalIgnoreCase))
            {
                return HotCornerActions.None;
            }

            if (string.Equals(actionId, HotCornerActions.Snapshot, StringComparison.OrdinalIgnoreCase))
            {
                return ActionPinStore.GetProviderActionKey("WorkspaceProvider", HotCornerActions.Snapshot);
            }

            if (string.Equals(actionId, "screenshot.capture", StringComparison.OrdinalIgnoreCase))
            {
                return ActionPinStore.GetProviderActionKey("ScreenshotProvider", "screenshot.capture");
            }

            if (string.Equals(actionId, HotCornerActions.ShowDesktop, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionId, HotCornerActions.TaskView, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionId, HotCornerActions.LockScreen, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionId, HotCornerActions.StartScreenSaver, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionId, HotCornerActions.TurnOffDisplay, StringComparison.OrdinalIgnoreCase))
            {
                return ActionPinStore.GetProviderActionKey("SystemControlsProvider", actionId);
            }

            return actionId.Trim();
        }
    }
}
