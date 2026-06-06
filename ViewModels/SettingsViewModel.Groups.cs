// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using TopToolbar.Logging;
using TopToolbar.Models;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel
    {
        public const string DefaultGroupsId = "default-groups";
        public const string DefaultGroupsName = "Built-in";
        private const string DefaultGroupsDescription = "Built-in";

        public ObservableCollection<ButtonGroup> Groups { get; } = new();

        private ButtonGroup _selectedGroup;

        public ButtonGroup SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (!ReferenceEquals(_selectedGroup, value))
                {
                    SetProperty(ref _selectedGroup, value);
                    OnPropertyChanged(nameof(HasSelectedGroup));
                    OnPropertyChanged(nameof(HasNoSelectedGroup));
                    if (value != null)
                    {
                        // Deselect General when a group is selected
                        _isGeneralSelected = false;
                        OnPropertyChanged(nameof(IsGeneralSelected));
                        if (SelectedWorkspace != null)
                        {
                            SelectedWorkspace = null;
                        }
                    }
                    // Must update IsGroupSelected after IsGeneralSelected is updated
                    OnPropertyChanged(nameof(IsGroupSelected));
                    OnPropertyChanged(nameof(IsWorkspaceSelected));
                    OnPropertyChanged(nameof(IsUserGroupSelected));
                    OnPropertyChanged(nameof(IsDefaultActionsGroupSelected));
                    OnPropertyChanged(nameof(CanRemoveSelectedGroup));
                    OnPropertyChanged(nameof(CanAddButtonToSelectedGroup));
                }
            }
        }

        private ToolbarButton _selectedButton;

        public ToolbarButton SelectedButton
        {
            get => _selectedButton;
            set
            {
                SetProperty(ref _selectedButton, value);
                OnPropertyChanged(nameof(HasSelectedButton));
            }
        }

        public bool HasSelectedGroup => SelectedGroup != null;

        public bool HasNoSelectedGroup => SelectedGroup == null;

        public bool HasSelectedButton => SelectedButton != null;

        private bool _isGeneralSelected = true;

        public bool IsGeneralSelected
        {
            get => _isGeneralSelected;
            set
            {
                if (_isGeneralSelected != value)
                {
                    _isGeneralSelected = value;
                    OnPropertyChanged(nameof(IsGeneralSelected));
                    OnPropertyChanged(nameof(IsGroupSelected));
                    OnPropertyChanged(nameof(IsWorkspaceSelected));
                    OnPropertyChanged(nameof(IsUserGroupSelected));
                    OnPropertyChanged(nameof(IsDefaultActionsGroupSelected));
                    OnPropertyChanged(nameof(CanRemoveSelectedGroup));
                    OnPropertyChanged(nameof(CanAddButtonToSelectedGroup));
                    if (value)
                    {
                        // Deselect group when General is selected
                        SelectedGroup = null;
                        SelectedWorkspace = null;
                    }
                }
            }
        }

        public bool IsGroupSelected => !IsGeneralSelected && SelectedGroup != null;

        public bool IsUserGroupSelected => IsGroupSelected && !IsLockedDefaultGroup(SelectedGroup);

        public bool IsDefaultActionsGroupSelected => IsGroupSelected && IsLockedDefaultGroup(SelectedGroup);

        public bool CanRemoveSelectedGroup => IsUserGroupSelected;

        public bool CanAddButtonToSelectedGroup => IsUserGroupSelected;

        public bool IsLockedGroup(ButtonGroup group) => IsLockedDefaultGroup(group);

        public void EnsureDefaultActionsGroupEntry()
        {
            var existing = Groups.FirstOrDefault(IsLockedDefaultGroup);
            if (existing != null)
            {
                existing.Name = DefaultGroupsName;
                existing.Description = DefaultGroupsDescription;
                existing.IsEnabled = true;
                return;
            }

            var defaultGroup = new ButtonGroup
            {
                Id = DefaultGroupsId,
                Name = DefaultGroupsName,
                Description = DefaultGroupsDescription,
                IsEnabled = true,
                Layout = new ToolbarGroupLayout
                {
                    Style = ToolbarGroupLayoutStyle.Icon,
                    Overflow = ToolbarGroupOverflowMode.Wrap,
                },
            };

            Groups.Insert(0, defaultGroup);
        }

        private void Groups_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.Cast<ButtonGroup>())
                {
                    HookGroup(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.Cast<ButtonGroup>())
                {
                    UnhookGroup(item);
                }
            }

            ScheduleSave();
        }

        private void HookGroup(ButtonGroup group)
        {
            if (group == null)
            {
                return;
            }

            group.PropertyChanged += Group_PropertyChanged;
            group.Buttons.CollectionChanged += Buttons_CollectionChanged;
            foreach (var b in group.Buttons)
            {
                HookButton(b);
            }
        }

        private void UnhookGroup(ButtonGroup group)
        {
            if (group == null)
            {
                return;
            }

            group.PropertyChanged -= Group_PropertyChanged;
            group.Buttons.CollectionChanged -= Buttons_CollectionChanged;
            foreach (var b in group.Buttons)
            {
                UnhookButton(b);
            }
        }

        private void Buttons_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.Cast<ToolbarButton>())
                {
                    HookButton(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.Cast<ToolbarButton>())
                {
                    UnhookButton(item);
                }
                // Only save when buttons are removed, not added
                // New buttons with empty command should not trigger save
                ScheduleSave();
            }
        }

        private void Group_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ScheduleSave();
        }

        private void HookButton(ToolbarButton b)
        {
            if (b == null)
            {
                return;
            }

            b.PropertyChanged += Button_PropertyChanged;
            if (b.Action != null)
            {
                AppLogger.LogInfo($"HookButton: hooking Action.PropertyChanged for button '{b.Name}'");
                b.Action.PropertyChanged += (s, e) => OnActionPropertyChanged(b, e);
            }
            else
            {
                AppLogger.LogWarning($"HookButton: button '{b.Name}' has no Action");
            }
        }

        private void UnhookButton(ToolbarButton b)
        {
            if (b == null)
            {
                return;
            }

            b.PropertyChanged -= Button_PropertyChanged;
        }

        private void Button_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnActionPropertyChanged(ToolbarButton button, PropertyChangedEventArgs e)
        {
            AppLogger.LogInfo($"OnActionPropertyChanged: property={e.PropertyName}, button='{button?.Name}'");
            if (e.PropertyName == nameof(ToolbarAction.Command))
            {
                AppLogger.LogInfo($"OnActionPropertyChanged: Command changed to '{button?.Action?.Command}'");
                // Ensure property changes occur on UI thread
                if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        TryUpdateIconFromCommand(button);
                        ScheduleSave();
                    });
                }
                else
                {
                    TryUpdateIconFromCommand(button);
                    ScheduleSave();
                }
            }
        }

        public void AddGroup()
        {
            Groups.Add(new ButtonGroup { Name = "New Group" });
            SelectedGroup = Groups.LastOrDefault();
            SelectedButton = SelectedGroup?.Buttons.FirstOrDefault();
            ScheduleSave();
        }

        public void RemoveGroup(ButtonGroup group)
        {
            if (group == null || IsLockedDefaultGroup(group))
            {
                return;
            }

            Groups.Remove(group);
            if (SelectedGroup == group)
            {
                SelectedGroup = Groups.FirstOrDefault();
                SelectedButton = SelectedGroup?.Buttons.FirstOrDefault();
            }

            ScheduleSave();
        }

        public void AddButton(ButtonGroup group)
        {
            if (group == null || IsLockedDefaultGroup(group))
            {
                return;
            }

            var button = new ToolbarButton
            {
                Name = "New Button",
                Action = new ToolbarAction { Command = string.Empty },
                IsExpanded = true,
            };

            ResetIconToDefault(button);

            group.Buttons.Add(button);

            SelectedGroup = group;
            SelectedButton = group.Buttons.LastOrDefault();
            // Don't schedule save - wait until user fills in command
        }

        public void RemoveButton(ButtonGroup group, ToolbarButton button)
        {
            var removedIndex = group.Buttons.IndexOf(button);
            if (removedIndex < 0)
            {
                return;
            }

            group.Buttons.RemoveAt(removedIndex);

            if (SelectedButton == button)
            {
                if (group.Buttons.Count > 0)
                {
                    var newIndex = Math.Min(removedIndex, group.Buttons.Count - 1);
                    SelectedButton = group.Buttons[newIndex];
                }
                else
                {
                    SelectedButton = null;
                }
            }

            ScheduleSave();
        }

        private static bool IsLockedDefaultGroup(ButtonGroup group)
        {
            return group != null &&
                   string.Equals(group.Id, DefaultGroupsId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
