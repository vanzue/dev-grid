// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using TopToolbar.Models;
using TopToolbar.Stores;

namespace TopToolbar.ViewModels
{
    public sealed class ToolbarItemsViewModel : ObservableObject, IDisposable
    {
        private readonly ToolbarStore _store;
        private readonly Dictionary<string, ToolbarGroupViewModel> _groupMap = new(StringComparer.OrdinalIgnoreCase);
        private bool _showSettingsSeparator;

        public ToolbarItemsViewModel(ToolbarStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            VisibleGroups = new ObservableCollection<ToolbarGroupViewModel>();

            foreach (var group in _store.Groups)
            {
                UpsertGroup(group);
            }

            _store.Groups.CollectionChanged += OnStoreGroupsChanged;
            UpdateSeparators();
        }

        public event EventHandler LayoutChanged;

        public ObservableCollection<ToolbarGroupViewModel> VisibleGroups { get; }

        /// <summary>All group view-models in store order, regardless of bar visibility. The radial ring
        /// uses this so ring-only actions (whose group may not be shown on the bar) are still reachable.</summary>
        public IEnumerable<ToolbarGroupViewModel> AllGroups
        {
            get
            {
                foreach (var group in _store.Groups)
                {
                    if (group?.Id != null && _groupMap.TryGetValue(group.Id, out var vm))
                    {
                        yield return vm;
                    }
                }
            }
        }

        public bool ShowSettingsSeparator
        {
            get => _showSettingsSeparator;
            private set
            {
                if (_showSettingsSeparator != value)
                {
                    _showSettingsSeparator = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SettingsSeparatorVisibility));
                }
            }
        }

        public Visibility SettingsSeparatorVisibility =>
            ShowSettingsSeparator ? Visibility.Visible : Visibility.Collapsed;

        public void Dispose()
        {
            _store.Groups.CollectionChanged -= OnStoreGroupsChanged;

            foreach (var vm in _groupMap.Values)
            {
                vm.VisibilityChanged -= OnGroupVisibilityChanged;
                vm.ButtonsChanged -= OnGroupButtonsChanged;
                vm.Dispose();
            }

            _groupMap.Clear();
            VisibleGroups.Clear();
        }

        private void OnStoreGroupsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e == null || e.Action == NotifyCollectionChangedAction.Reset)
            {
                ResetAllGroups();
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                ResetAllGroups();
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    UpsertGroup(item as ButtonGroup);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    RemoveGroup(item as ButtonGroup);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    UpsertGroup(item as ButtonGroup);
                }
            }

            UpdateSeparators();
            OnLayoutChanged();
        }

        private void ResetAllGroups()
        {
            foreach (var vm in _groupMap.Values)
            {
                vm.VisibilityChanged -= OnGroupVisibilityChanged;
                vm.ButtonsChanged -= OnGroupButtonsChanged;
                vm.Dispose();
            }

            _groupMap.Clear();
            VisibleGroups.Clear();

            foreach (var group in _store.Groups)
            {
                UpsertGroup(group);
            }

            UpdateSeparators();
            OnLayoutChanged();
        }

        private void UpsertGroup(ButtonGroup group)
        {
            if (group == null || string.IsNullOrWhiteSpace(group.Id))
            {
                return;
            }

            if (!_groupMap.TryGetValue(group.Id, out var vm))
            {
                vm = new ToolbarGroupViewModel(group);
                vm.VisibilityChanged += OnGroupVisibilityChanged;
                vm.ButtonsChanged += OnGroupButtonsChanged;
                _groupMap[group.Id] = vm;
            }
            else
            {
                vm.SetGroup(group);
            }

            UpdateGroupVisibility(vm);
        }

        private void RemoveGroup(ButtonGroup group)
        {
            if (group == null || string.IsNullOrWhiteSpace(group.Id))
            {
                return;
            }

            if (_groupMap.TryGetValue(group.Id, out var vm))
            {
                vm.VisibilityChanged -= OnGroupVisibilityChanged;
                vm.ButtonsChanged -= OnGroupButtonsChanged;
                _groupMap.Remove(group.Id);
                VisibleGroups.Remove(vm);
                vm.Dispose();
            }
        }

        private void OnGroupVisibilityChanged(object sender, EventArgs e)
        {
            if (sender is ToolbarGroupViewModel vm)
            {
                UpdateGroupVisibility(vm);
                UpdateSeparators();
                OnLayoutChanged();
            }
        }

        private void OnGroupButtonsChanged(object sender, EventArgs e)
        {
            if (sender is ToolbarGroupViewModel vm && vm.IsVisible)
            {
                UpdateSeparators();
                OnLayoutChanged();
            }
        }

        private void UpdateGroupVisibility(ToolbarGroupViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            if (vm.IsVisible)
            {
                if (!VisibleGroups.Contains(vm))
                {
                    int insertIndex = GetInsertIndex(vm.GroupId);
                    VisibleGroups.Insert(insertIndex, vm);
                }
            }
            else
            {
                VisibleGroups.Remove(vm);
            }
        }

        private int GetInsertIndex(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return VisibleGroups.Count;
            }

            int index = 0;
            foreach (var group in _store.Groups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.Id))
                {
                    continue;
                }

                if (string.Equals(group.Id, groupId, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (_groupMap.TryGetValue(group.Id, out var vm) && vm.IsVisible)
                {
                    index++;
                }
            }

            return Math.Min(index, VisibleGroups.Count);
        }

        private void UpdateSeparators()
        {
            for (int i = 0; i < VisibleGroups.Count; i++)
            {
                VisibleGroups[i].ShowTrailingSeparator = i < VisibleGroups.Count - 1;
            }

            ShowSettingsSeparator = VisibleGroups.Count > 0;
        }

        private void OnLayoutChanged()
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
