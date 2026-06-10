// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using TopToolbar.Models;

namespace TopToolbar.ViewModels
{
    public sealed class ToolbarButtonItem : ObservableObject, IDisposable
    {
        public ToolbarButtonItem(ButtonGroup group, ToolbarButton button)
        {
            Group = group ?? throw new ArgumentNullException(nameof(group));
            Button = button ?? throw new ArgumentNullException(nameof(button));

            Group.PropertyChanged += OnGroupPropertyChanged;
            Button.PropertyChanged += OnButtonPropertyChanged;
        }

        public ButtonGroup Group { get; }

        public ToolbarButton Button { get; }

        public bool IsEnabled => Group.IsEnabled && Button.IsActionEnabled && (!Button.IsDimmed || Button.CanExecuteWhenDimmed);

        public double GroupOpacity
        {
            get
            {
                if (!Group.IsEnabled)
                {
                    return 0.38;
                }

                return Button.IsDimmed ? 0.58 : 1.0;
            }
        }

        public double IconOpacity
        {
            get
            {
                if (!Group.IsEnabled)
                {
                    return 0.28;
                }

                if (Button.IsDimmed)
                {
                    return 0.46;
                }

                return Button.IconOpacity;
            }
        }

        public Visibility ProgressVisibility => Group.IsEnabled ? Button.ProgressVisibility : Visibility.Collapsed;

        public void Dispose()
        {
            Group.PropertyChanged -= OnGroupPropertyChanged;
            Button.PropertyChanged -= OnButtonPropertyChanged;
        }

        private void OnGroupPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || e.PropertyName == nameof(ButtonGroup.IsEnabled))
            {
                NotifyStateChanged();
            }
        }

        private void OnButtonPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                NotifyStateChanged();
                return;
            }

            if (e.PropertyName == nameof(ToolbarButton.IsEnabled) ||
                e.PropertyName == nameof(ToolbarButton.IsDimmed) ||
                e.PropertyName == nameof(ToolbarButton.CanExecuteWhenDimmed) ||
                e.PropertyName == nameof(ToolbarButton.IsExecuting) ||
                e.PropertyName == nameof(ToolbarButton.IconOpacity) ||
                e.PropertyName == nameof(ToolbarButton.ProgressVisibility))
            {
                NotifyStateChanged();
            }
        }

        private void NotifyStateChanged()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(GroupOpacity));
            OnPropertyChanged(nameof(IconOpacity));
            OnPropertyChanged(nameof(ProgressVisibility));
        }
    }
}
