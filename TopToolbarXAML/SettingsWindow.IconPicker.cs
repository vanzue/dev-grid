// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Services;
using TopToolbar.ViewModels;

namespace TopToolbar
{
    public sealed partial class SettingsWindow
    {
        private static readonly char[] IconSearchSeparators = { ' ' };

        private async void OnChooseIconFromLibrary(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            object target = element?.DataContext;
            if (target == null)
            {
                if (_vm.SelectedButton != null)
                {
                    target = _vm.SelectedButton;
                }
                else if (_vm.SelectedWorkspace != null)
                {
                    target = _vm.SelectedWorkspace;
                }
            }

            if (target is not ToolbarButton && target is not WorkspaceButtonViewModel)
            {
                return;
            }

            if (Content is not FrameworkElement root)
            {
                return;
            }

            try
            {
                var allItems = BuildIconPickerItems();
                if (allItems.Count == 0)
                {
                    return;
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = "Choose an icon",
                    PrimaryButtonText = "Use icon",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    IsPrimaryButtonEnabled = false,
                    MinWidth = 420,
                };
                dialog.MaxWidth = 640;
                dialog.MaxHeight = 600;

                var gridView = new GridView
                {
                    SelectionMode = ListViewSelectionMode.Single,
                    IsItemClickEnabled = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    MinHeight = 280,
                    Padding = new Thickness(4, 0, 4, 4),
                };

                // Set up item template using code
                gridView.ItemTemplate = CreateIconItemTemplate();
                SetupIconGridViewItemBinding(gridView);

                ScrollViewer.SetVerticalScrollMode(gridView, ScrollMode.Enabled);
                ScrollViewer.SetVerticalScrollBarVisibility(gridView, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollMode(gridView, ScrollMode.Disabled);

                var emptyState = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false,
                };
                emptyState.Children.Add(new FontIcon
                {
                    Glyph = "\uE737",
                    FontSize = 36,
                    Opacity = 0.35,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                });
                emptyState.Children.Add(new TextBlock
                {
                    Text = "No icons match your search.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = 0.65,
                });

                var contentHost = new Grid();
                contentHost.Children.Add(gridView);
                contentHost.Children.Add(emptyState);

                var layout = new Grid
                {
                    MinWidth = 420,
                    MaxWidth = 640,
                    MinHeight = 360,
                    MaxHeight = 520,
                    RowSpacing = 8,
                };

                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var searchBox = new AutoSuggestBox
                {
                    PlaceholderText = "Search icons",
                    Margin = new Thickness(0, 0, 0, 4),
                    QueryIcon = new SymbolIcon(Symbol.Find),
                };

                searchBox.Loaded += (s, _) => searchBox.Focus(FocusState.Programmatic);

                Grid.SetRow(searchBox, 0);
                layout.Children.Add(searchBox);

                Grid.SetRow(contentHost, 1);
                layout.Children.Add(contentHost);

                dialog.Content = layout;

                IconPickerItem selectedItem = DetermineInitialSelection(target, allItems);
                string selectedId = selectedItem?.Id;
                List<IconPickerItem> filteredItems = new(allItems);

                void ApplySelection(IconPickerItem item, bool updateView = true)
                {
                    if (item == null)
                    {
                        selectedItem = null;
                        selectedId = null;

                        if (updateView)
                        {
                            gridView.SelectedIndex = -1;
                        }

                        dialog.IsPrimaryButtonEnabled = false;
                        return;
                    }

                    selectedId = item.Id;
                    selectedItem = allItems.FirstOrDefault(candidate => candidate.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? item;

                    if (updateView)
                    {
                        var index = filteredItems.FindIndex(candidate => candidate.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0)
                        {
                            gridView.SelectedIndex = index;
                            gridView.UpdateLayout();
                            gridView.ScrollIntoView(filteredItems[index], ScrollIntoViewAlignment.Leading);
                        }
                    }

                    dialog.IsPrimaryButtonEnabled = true;
                }

                List<IconPickerItem> FilterItems(string query)
                {
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        return new List<IconPickerItem>(allItems);
                    }

                    var tokens = query.Split(IconSearchSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length == 0)
                    {
                        return new List<IconPickerItem>(allItems);
                    }

                    return allItems.Where(item => tokens.All(item.MatchesTerm)).ToList();
                }

                void RefreshFilteredItems(string query, bool scrollToSelection)
                {
                    filteredItems = FilterItems(query);
                    gridView.ItemsSource = filteredItems;

                    var hasItems = filteredItems.Count > 0;
                    gridView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
                    emptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

                    if (!hasItems)
                    {
                        ApplySelection(null, updateView: false);
                        return;
                    }

                    if (!string.IsNullOrEmpty(selectedId))
                    {
                        var match = filteredItems.FirstOrDefault(candidate => candidate.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            ApplySelection(match, updateView: scrollToSelection);
                            return;
                        }
                    }

                    gridView.SelectedIndex = -1;
                    dialog.IsPrimaryButtonEnabled = false;
                }

                gridView.SelectionChanged += (s, args) =>
                {
                    if (gridView.SelectedItem is IconPickerItem item)
                    {
                        ApplySelection(item, updateView: false);
                    }
                    else
                    {
                        ApplySelection(null, updateView: false);
                    }
                };

                gridView.ItemClick += (s, args) =>
                {
                    if (args.ClickedItem is IconPickerItem item)
                    {
                        ApplySelection(item, updateView: false);
                        ApplyIconSelection(target, item);
                        AppLogger.LogInfo($"Icon picker quick select: {item.DisplayName} [{item.IconType}] id={item.Id}");
                        try
                        {
                            dialog.Hide();
                        }
                        catch (Exception hideEx)
                        {
                            AppLogger.LogWarning($"Icon picker dialog hide failed: {hideEx.Message}");
                        }
                    }
                };

                searchBox.TextChanged += (s, args) =>
                {
                    if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen)
                    {
                        return;
                    }

                    RefreshFilteredItems(searchBox.Text, scrollToSelection: false);
                };

                searchBox.QuerySubmitted += (s, args) =>
                {
                    RefreshFilteredItems(args.QueryText ?? searchBox.Text, scrollToSelection: true);
                };

                RefreshFilteredItems(searchBox.Text, scrollToSelection: true);

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && selectedItem != null)
                {
                    AppLogger.LogInfo($"Icon picker primary select: {selectedItem.DisplayName} [{selectedItem.IconType}] id={selectedItem.Id}");
                    ApplyIconSelection(target, selectedItem);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("OnChooseIconFromLibrary failed", ex);
            }
        }

        private void ApplyIconSelection(object target, IconPickerItem item)
        {
            if (target == null || item == null)
            {
                return;
            }

            switch (target)
            {
                case ToolbarButton button:
                    button.IsIconCustomized = true;
                    if (!string.IsNullOrWhiteSpace(item.CatalogId))
                    {
                        _vm.TrySetCatalogIcon(button, item.CatalogId);
                    }
                    else if (!string.IsNullOrWhiteSpace(item.ImagePath))
                    {
                        _vm.TrySetImageIcon(button, item.ImagePath);
                    }

                    break;

                case WorkspaceButtonViewModel workspace:
                    if (!string.IsNullOrWhiteSpace(item.CatalogId))
                    {
                        _vm.TrySetWorkspaceCatalogIcon(workspace, item.CatalogId);
                    }
                    else if (!string.IsNullOrWhiteSpace(item.Glyph))
                    {
                        _vm.TrySetWorkspaceGlyphIcon(workspace, item.Glyph);
                    }
                    else if (!string.IsNullOrWhiteSpace(item.ImagePath))
                    {
                        _ = _vm.TrySetWorkspaceImageIconFromFileAsync(workspace, item.ImagePath);
                    }

                    break;
            }
        }

        private IconPickerItem DetermineInitialSelection(object target, List<IconPickerItem> allItems)
        {
            if (target == null || allItems == null || allItems.Count == 0)
            {
                return null;
            }

            switch (target)
            {
                case ToolbarButton button:
                    if (button != null && IconCatalogService.TryParseCatalogId(button.IconPath, out var catalogId))
                    {
                        return allItems.FirstOrDefault(item => string.Equals(item.CatalogId, catalogId, StringComparison.OrdinalIgnoreCase));
                    }

                    break;

                case WorkspaceButtonViewModel workspace:
                    var icon = workspace.Icon;
                    if (icon == null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(icon.CatalogId))
                    {
                        return allItems.FirstOrDefault(item => string.Equals(item.CatalogId, icon.CatalogId, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!string.IsNullOrWhiteSpace(icon.Glyph))
                    {
                        return allItems.FirstOrDefault(item => string.Equals(item.Glyph, icon.Glyph, StringComparison.OrdinalIgnoreCase));
                    }

                    break;
            }

            return null;
        }

        private List<IconPickerItem> BuildIconPickerItems()
        {
            return IconCatalogService.GetAll()
                .Where(entry => entry != null)
                .Select(IconPickerItem.FromCatalog)
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static DataTemplate CreateIconItemTemplate()
        {
            // Create the item template with x:Bind style using Loaded event workaround.
            const string templateXaml = @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                                  xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                <StackPanel x:Name='RootPanel' Width='96' Height='120' HorizontalAlignment='Center' Spacing='6' Tag='{Binding}'>
                    <Border x:Name='IconBorder' Width='72' Height='72' CornerRadius='18' BorderThickness='1'>
                        <Grid x:Name='IconHost'/>
                    </Border>
                    <TextBlock x:Name='NameText' TextAlignment='Center' FontWeight='SemiBold' TextWrapping='WrapWholeWords' MaxLines='2'/>
                    <TextBlock x:Name='CategoryText' TextAlignment='Center' FontSize='10' Opacity='0.6' TextWrapping='NoWrap' TextTrimming='CharacterEllipsis'/>
                </StackPanel>
            </DataTemplate>";

            return (DataTemplate)XamlReader.Load(templateXaml);
        }

        private void SetupIconGridViewItemBinding(GridView gridView)
        {
            gridView.ContainerContentChanging += (sender, args) =>
            {
                if (args.InRecycleQueue)
                {
                    return;
                }

                if (args.Phase == 0)
                {
                    args.RegisterUpdateCallback(PopulateIconItem);
                    args.Handled = true;
                }
            };
        }

        private void PopulateIconItem(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not IconPickerItem item)
            {
                return;
            }

            var templateRoot = args.ItemContainer.ContentTemplateRoot as StackPanel;
            if (templateRoot == null)
            {
                return;
            }

            // Find child elements by name
            var iconBorder = templateRoot.FindName("IconBorder") as Border;
            var iconHost = templateRoot.FindName("IconHost") as Grid;
            var nameText = templateRoot.FindName("NameText") as TextBlock;
            var categoryText = templateRoot.FindName("CategoryText") as TextBlock;

            if (iconBorder != null)
            {
                try
                {
                    iconBorder.Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
                    iconBorder.BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush;
                }
                catch { }
            }

            if (iconHost != null)
            {
                iconHost.Children.Clear();

                if (item.SvgSource != null)
                {
                    var image = new Image
                    {
                        Width = 48,
                        Height = 48,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                        Source = item.SvgSource,
                    };
                    iconHost.Children.Add(image);
                }
                else if (!string.IsNullOrWhiteSpace(item.Glyph))
                {
                    Brush foreground = null;
                    try
                    {
                        foreground = Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush;
                    }
                    catch { }

                    var fontIcon = new FontIcon
                    {
                        Glyph = item.Glyph,
                        FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                        FontSize = 28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    if (foreground != null)
                    {
                        fontIcon.Foreground = foreground;
                    }

                    iconHost.Children.Add(fontIcon);
                }
            }

            if (nameText != null)
            {
                nameText.Text = item.DisplayName ?? string.Empty;
            }

            if (categoryText != null)
            {
                categoryText.Text = item.Category ?? string.Empty;
            }
        }

        private sealed class IconPickerItem
        {
            private IconPickerItem(string id, string displayName, string category, ToolbarIconType iconType, Uri imageUri, string glyph, string catalogId, string imagePath, IReadOnlyList<string> keywords)
            {
                Id = id;
                DisplayName = displayName;
                Category = category;
                IconType = iconType;
                ImageUri = imageUri;
                Glyph = glyph;
                CatalogId = catalogId;
                ImagePath = imagePath;
                Keywords = keywords ?? Array.Empty<string>();

                // Pre-create SvgImageSource for binding
                if (imageUri != null)
                {
                    SvgSource = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(imageUri)
                    {
                        RasterizePixelWidth = 48,
                        RasterizePixelHeight = 48,
                    };
                }
            }

            public string Id { get; }

            public string DisplayName { get; }

            public string Category { get; }

            public ToolbarIconType IconType { get; }

            public Uri ImageUri { get; }

            public string Glyph { get; }

            public string CatalogId { get; }

            public string ImagePath { get; }

            public IReadOnlyList<string> Keywords { get; }

            public Microsoft.UI.Xaml.Media.Imaging.SvgImageSource SvgSource { get; }

            public Visibility SvgVisibility => SvgSource != null ? Visibility.Visible : Visibility.Collapsed;

            public Visibility GlyphVisibility => !string.IsNullOrWhiteSpace(Glyph) ? Visibility.Visible : Visibility.Collapsed;

            public bool MatchesTerm(string term)
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    return true;
                }

                var comparison = StringComparison.OrdinalIgnoreCase;

                bool Contains(string source) => !string.IsNullOrWhiteSpace(source) && source.Contains(term, comparison);

                if (Contains(DisplayName) || Contains(Category) || Contains(Glyph) || Contains(CatalogId))
                {
                    return true;
                }

                if (Keywords != null)
                {
                    foreach (var keyword in Keywords)
                    {
                        if (Contains(keyword))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            public static IconPickerItem FromCatalog(IconCatalogEntry entry)
            {
                return new IconPickerItem(
                    id: "catalog-" + entry.Id,
                    displayName: entry.DisplayName,
                    category: string.IsNullOrWhiteSpace(entry.Category) ? "Catalog" : entry.Category,
                    iconType: ToolbarIconType.Catalog,
                    imageUri: entry.ResourceUri,
                    glyph: entry.Glyph,
                    catalogId: entry.Id,
                    imagePath: string.Empty,
                    keywords: entry.Keywords ?? Array.Empty<string>());
            }
        }
    }
}
