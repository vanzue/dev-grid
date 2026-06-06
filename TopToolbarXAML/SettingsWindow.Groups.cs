// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Models;

namespace TopToolbar
{
    public sealed partial class SettingsWindow
    {
        private void OnToggleGroupsPane(object sender, RoutedEventArgs e)
        {
            EnsureLeftPaneColumn();
            if (_leftPaneColumnCache != null)
            {
                _leftPaneColumnCache.Width =
                    (_leftPaneColumnCache.Width.Value == 0)
                        ? new GridLength(240)
                        : new GridLength(0);
            }
        }

        private async void OnAddGroup(object sender, RoutedEventArgs e)
        {
            _vm.AddGroup();
            await _vm.SaveAsync();
        }

        private async void OnRemoveGroup(object sender, RoutedEventArgs e)
        {
            var tag = (sender as Button)?.Tag;
            var group =
                (tag as ButtonGroup)
                ?? (_vm.Groups.Contains(_vm.SelectedGroup) ? _vm.SelectedGroup : null);
            if (group != null)
            {
                _vm.RemoveGroup(group);
                await _vm.SaveAsync();
            }
        }

        private async void OnRemoveSelectedGroup(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedGroup != null && _vm.CanRemoveSelectedGroup)
            {
                _vm.RemoveGroup(_vm.SelectedGroup);
                await _vm.SaveAsync();
            }
        }

        private void OnAddButton(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedGroup != null && _vm.CanAddButtonToSelectedGroup)
            {
                _vm.AddButton(_vm.SelectedGroup);
                // Don't save immediately - new button has empty command
                // Save will happen when user fills in command or closes settings
            }
        }

        private void OnGeneralItemTapped(object sender, TappedRoutedEventArgs e)
        {
            _vm.IsGeneralSelected = true;
            // Deselect any group in the ListView
            GroupsList.SelectedItem = null;
            WorkspacesList.SelectedItem = null;
        }

        // Rename handlers for groups list
        private async void OnStartRenameGroup(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is ButtonGroup group)
                {
                    if (_vm.IsLockedGroup(group))
                    {
                        return;
                    }

                    await PromptRenameGroupAsync(group);
                }
            }
            catch (Exception ex)
            {
                SafeLogWarning("OnStartRenameGroup: " + ex.Message);
            }
        }

        private async Task PromptRenameGroupAsync(ButtonGroup group)
        {
            if (group == null)
            {
                return;
            }

            var input = new TextBox
            {
                Text = group.Name ?? string.Empty,
                MinWidth = 320,
                PlaceholderText = "Group name",
            };

            var dialog = new ContentDialog
            {
                Title = "Rename Group",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = input,
                XamlRoot = (this.Content as FrameworkElement)?.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var newName = (input.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, group.Name, StringComparison.Ordinal))
            {
                return;
            }

            group.Name = newName;
            await _vm.SaveAsync();
        }

        private void CommitGroupRename(TextBox editBox, TextBlock textBlock)
        {
            if (editBox == null || textBlock == null)
            {
                return;
            }

            textBlock.Visibility = Visibility.Visible;
            editBox.Visibility = Visibility.Collapsed;
        }

        private void OnGroupNameTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    var parent = tb.Parent as FrameworkElement;
                    var textBlock = FindSibling<TextBlock>(tb, "NameText");
                    CommitGroupRename(tb, textBlock);
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    // Revert displayed text (binding already updated progressively, so we reload from VM selected group name)
                    if (_vm.SelectedGroup != null)
                    {
                        tb.Text = _vm.SelectedGroup.Name;
                    }

                    var textBlock = FindSibling<TextBlock>(tb, "NameText");
                    CommitGroupRename(tb, textBlock);
                    e.Handled = true;
                }
            }
        }

        private void OnGroupNameTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                var textBlock = FindSibling<TextBlock>(tb, "NameText");
                CommitGroupRename(tb, textBlock);
            }
        }

        // Utility visual tree search helpers
        private static T FindChild<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            if (root == null)
            {
                return null;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T fe)
                {
                    if (string.IsNullOrEmpty(name) || fe.Name == name)
                    {
                        return fe;
                    }
                }

                var result = FindChild<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static T FindSibling<T>(FrameworkElement element, string name)
            where T : FrameworkElement
        {
            if (element?.Parent is DependencyObject parent)
            {
                return FindChild<T>(parent, name);
            }

            return null;
        }
    }
}
