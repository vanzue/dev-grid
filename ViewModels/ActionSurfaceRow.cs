// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using TopToolbar.Models;

namespace TopToolbar.ViewModels
{
    /// <summary>
    /// One row in the "Bar &amp; Ring" settings matrix: an action plus its bar/ring placement toggles.
    /// </summary>
    public sealed class ActionSurfaceRow : INotifyPropertyChanged
    {
        private readonly Action<ActionSurfaceRow, bool> _onToggle;
        private bool _isOnBar;
        private bool _isOnRing;
        private bool _suppress;

        public ActionSurfaceRow(
            string key,
            string providerId,
            string label,
            string glyph,
            ActionSurfaces baseSurfaces,
            Action<ActionSurfaceRow, bool> onToggle)
        {
            Key = key ?? string.Empty;
            ProviderId = providerId ?? string.Empty;
            Label = string.IsNullOrWhiteSpace(label) ? Key : label;
            Glyph = string.IsNullOrWhiteSpace(glyph) ? "\uE700" : glyph;
            BaseSurfaces = baseSurfaces;
            _isOnBar = (baseSurfaces & ActionSurfaces.Bar) != 0;
            _isOnRing = (baseSurfaces & ActionSurfaces.Ring) != 0;
            _onToggle = onToggle;
        }

        public string Key { get; }

        public string ProviderId { get; }

        public string Label { get; }

        public string Glyph { get; }

        public ActionSurfaces BaseSurfaces { get; set; }

        public bool IsOnBar
        {
            get => _isOnBar;
            set
            {
                if (_isOnBar != value)
                {
                    _isOnBar = value;
                    OnPropertyChanged(nameof(IsOnBar));
                    if (!_suppress)
                    {
                        _onToggle?.Invoke(this, true);
                    }
                }
            }
        }

        public bool IsOnRing
        {
            get => _isOnRing;
            set
            {
                if (_isOnRing != value)
                {
                    _isOnRing = value;
                    OnPropertyChanged(nameof(IsOnRing));
                    if (!_suppress)
                    {
                        _onToggle?.Invoke(this, false);
                    }
                }
            }
        }

        /// <summary>Reverts a toggle without re-triggering the change callback (used to enforce
        /// "an action can't be removed from every surface").</summary>
        public void RevertSilently(bool isBar, bool value)
        {
            _suppress = true;
            if (isBar)
            {
                IsOnBar = value;
            }
            else
            {
                IsOnRing = value;
            }

            _suppress = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
