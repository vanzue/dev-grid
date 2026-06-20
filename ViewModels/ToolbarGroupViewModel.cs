// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using TopToolbar.Models;

namespace TopToolbar.ViewModels
{
    public sealed class ToolbarGroupViewModel : ObservableObject, IDisposable
    {
        private ButtonGroup _group;
        private ObservableCollection<ToolbarButton> _buttonsSource;
        private readonly HashSet<ToolbarButton> _trackedButtons = new();
        private bool _isVisible;
        private bool _showTrailingSeparator;

        public ToolbarGroupViewModel(ButtonGroup group)
        {
            Buttons = new ObservableCollection<ToolbarButtonItem>();
            SetGroup(group);
        }

        public event EventHandler VisibilityChanged;

        public event EventHandler ButtonsChanged;

        public ButtonGroup Group => _group;

        public string GroupId => _group?.Id ?? string.Empty;

        public ObservableCollection<ToolbarButtonItem> Buttons { get; }

        // Ring placement is independent of the bar: the radial reads these (bar-filtered Buttons would
        // hide ring-only actions). Kept in sync with the source on every rebuild / surface change.
        public ObservableCollection<ToolbarButtonItem> RingButtons { get; } = new();

        public bool IsVisible
        {
            get => _isVisible;
            private set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    OnPropertyChanged();
                    VisibilityChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool ShowTrailingSeparator
        {
            get => _showTrailingSeparator;
            set
            {
                if (_showTrailingSeparator != value)
                {
                    _showTrailingSeparator = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TrailingSeparatorVisibility));
                }
            }
        }

        public Visibility TrailingSeparatorVisibility =>
            ShowTrailingSeparator ? Visibility.Visible : Visibility.Collapsed;

        public void SetGroup(ButtonGroup group)
        {
            if (ReferenceEquals(_group, group))
            {
                return;
            }

            DetachGroup();
            _group = group;
            AttachGroup();
            RebuildButtons();
            UpdateVisibility();
        }

        public void Dispose()
        {
            DetachGroup();
        }

        private void AttachGroup()
        {
            if (_group == null)
            {
                return;
            }

            _group.PropertyChanged += OnGroupPropertyChanged;
            AttachButtonsCollection(_group.Buttons);
        }

        private void DetachGroup()
        {
            if (_group == null)
            {
                return;
            }

            _group.PropertyChanged -= OnGroupPropertyChanged;
            DetachButtonsCollection();
            _group = null;
        }

        private void AttachButtonsCollection(ObservableCollection<ToolbarButton> buttons)
        {
            _buttonsSource = buttons ?? new ObservableCollection<ToolbarButton>();
            _buttonsSource.CollectionChanged += OnButtonsCollectionChanged;
        }

        private void DetachButtonsCollection()
        {
            if (_buttonsSource != null)
            {
                _buttonsSource.CollectionChanged -= OnButtonsCollectionChanged;
                _buttonsSource = null;
            }

            foreach (var button in _trackedButtons)
            {
                button.PropertyChanged -= OnButtonPropertyChanged;
            }

            _trackedButtons.Clear();

            foreach (var item in Buttons)
            {
                item.Dispose();
            }

            Buttons.Clear();

            foreach (var item in RingButtons)
            {
                item.Dispose();
            }

            RingButtons.Clear();
        }

        private void RebuildButtons()
        {
            foreach (var item in Buttons)
            {
                item.Dispose();
            }

            Buttons.Clear();
            foreach (var button in _trackedButtons)
            {
                button.PropertyChanged -= OnButtonPropertyChanged;
            }

            _trackedButtons.Clear();

            if (_buttonsSource == null)
            {
                OnButtonsChanged();
                return;
            }

            foreach (var button in _buttonsSource)
            {
                if (button == null)
                {
                    continue;
                }

                SubscribeButton(button);
                if (IsBarVisible(button))
                {
                    Buttons.Add(new ToolbarButtonItem(_group, button));
                }
            }

            RebuildRingButtons();
            OnButtonsChanged();
        }

        private static bool IsBarVisible(ToolbarButton button)
        {
            return button != null && button.IsEnabled && (button.Surfaces & ActionSurfaces.Bar) != 0;
        }

        private static bool IsRingVisible(ToolbarButton button)
        {
            return button != null && button.IsEnabled && (button.Surfaces & ActionSurfaces.Ring) != 0;
        }

        private void RebuildRingButtons()
        {
            foreach (var item in RingButtons)
            {
                item.Dispose();
            }

            RingButtons.Clear();

            if (_buttonsSource == null)
            {
                return;
            }

            foreach (var button in _buttonsSource)
            {
                if (IsRingVisible(button))
                {
                    RingButtons.Add(new ToolbarButtonItem(_group, button));
                }
            }
        }

        private void OnButtonsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e == null)
            {
                RebuildButtons();
                UpdateVisibility();
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                RebuildButtons();
                UpdateVisibility();
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                RebuildButtons();
                UpdateVisibility();
                return;
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is ToolbarButton button)
                    {
                        RemoveButton(button);
                        UnsubscribeButton(button);
                    }
                }
            }

            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is ToolbarButton button)
                    {
                        SubscribeButton(button);
                        if (IsBarVisible(button))
                        {
                            AddButton(button);
                        }
                    }
                }
            }

            UpdateVisibility();
            OnButtonsChanged();
        }

        private void OnGroupPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                UpdateVisibility();
                return;
            }

            if (e.PropertyName == nameof(ButtonGroup.IsEnabled))
            {
                UpdateVisibility();
                return;
            }

            if (e.PropertyName == nameof(ButtonGroup.Buttons))
            {
                DetachButtonsCollection();
                AttachButtonsCollection(_group.Buttons);
                RebuildButtons();
                UpdateVisibility();
            }
        }

        private void SubscribeButton(ToolbarButton button)
        {
            if (button == null || _trackedButtons.Contains(button))
            {
                return;
            }

            _trackedButtons.Add(button);
            button.PropertyChanged += OnButtonPropertyChanged;
        }

        private void UnsubscribeButton(ToolbarButton button)
        {
            if (button == null || !_trackedButtons.Remove(button))
            {
                return;
            }

            button.PropertyChanged -= OnButtonPropertyChanged;
        }

        private void OnButtonPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null ||
                e.PropertyName == nameof(ToolbarButton.IsEnabled) ||
                e.PropertyName == nameof(ToolbarButton.Surfaces))
            {
                if (sender is ToolbarButton button)
                {
                    if (IsBarVisible(button))
                    {
                        AddButton(button);
                    }
                    else
                    {
                        RemoveButton(button);
                    }

                    RebuildRingButtons();
                    UpdateVisibility();
                    OnButtonsChanged();
                }
            }
        }

        private void AddButton(ToolbarButton button)
        {
            if (button == null)
            {
                return;
            }

            if (FindButtonIndex(button) >= 0)
            {
                return;
            }

            int index = GetEnabledInsertIndex(button);
            Buttons.Insert(index, new ToolbarButtonItem(_group, button));
        }

        private void RemoveButton(ToolbarButton button)
        {
            if (button == null)
            {
                return;
            }

            int index = FindButtonIndex(button);
            if (index >= 0)
            {
                Buttons[index].Dispose();
                Buttons.RemoveAt(index);
            }
        }

        private int FindButtonIndex(ToolbarButton button)
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (ReferenceEquals(Buttons[i].Button, button))
                {
                    return i;
                }
            }

            return -1;
        }

        private int GetEnabledInsertIndex(ToolbarButton button)
        {
            if (_buttonsSource == null)
            {
                return Buttons.Count;
            }

            int index = 0;
            foreach (var candidate in _buttonsSource)
            {
                if (ReferenceEquals(candidate, button))
                {
                    break;
                }

                if (candidate != null && IsBarVisible(candidate))
                {
                    index++;
                }
            }

            return Math.Min(index, Buttons.Count);
        }

        private void UpdateVisibility()
        {
            bool visible = _group != null && Buttons.Count > 0;
            IsVisible = visible;
        }

        private void OnButtonsChanged()
        {
            ButtonsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
