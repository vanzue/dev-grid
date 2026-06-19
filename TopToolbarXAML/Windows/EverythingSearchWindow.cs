// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TopToolbar.Logging;
using TopToolbar.Services.Everything;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace TopToolbar;

internal sealed class EverythingSearchWindow : WindowEx, IDisposable
{
    private readonly EverythingSearchService _searchService;
    private readonly ObservableCollection<EverythingSearchResult> _results = new();
    private readonly TextBox _queryBox;
    private readonly TextBlock _statusText;
    private readonly ListView _resultsList;
    private readonly DispatcherQueueTimer _searchDebounceTimer;
    private readonly FrameworkElement _titleBar;
    private CancellationTokenSource _searchCts;
    private string _pendingQuery = string.Empty;
    private bool _disposed;
    private bool _contextMenuOpen;
    private readonly RecentSearchStore _recentStore = new();
    private TextBlock _recentHeader;

    // ----- Neutral palette pulled from the toolbar/notification theme (no accent hue) -----
    private readonly Brush _surfaceBrush;
    private readonly Brush _borderBrush;
    private readonly Color _labelColor;
    private readonly FontFamily _uiFont;

    private static SolidColorBrush Solid(Color c) => new(c);

    private SolidColorBrush Label(byte alpha) =>
        new(Color.FromArgb(alpha, _labelColor.R, _labelColor.G, _labelColor.B));

    public EverythingSearchWindow(EverythingSearchService searchService, ResourceDictionary themeResources = null)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));

        _surfaceBrush = CloneBrush(ReadBrush(themeResources, "ToolbarBackgroundBrush")) ?? CreateDefaultSurfaceBrush();
        _borderBrush = CloneBrush(ReadBrush(themeResources, "ToolbarBorderBrush")) ?? Solid(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        _labelColor = ResolveBrushColor(ReadBrush(themeResources, "ToolbarLabelBrush"), Color.FromArgb(0xFF, 0x2F, 0x3A, 0x3F));
        _uiFont = ReadFont(themeResources, "ToolbarTextFontFamily") ?? new FontFamily("Segoe UI Variable Text");

        Title = "Dev Grid Search";
        IsTitleBarVisible = true;
        ExtendsContentIntoTitleBar = true;
        MinWidth = 620;
        MinHeight = 420;
        Width = 820;
        Height = 560;

        var root = new Grid
        {
            Background = _surfaceBrush,
            Padding = new Thickness(22, 18, 22, 18),
        };

        var content = new Grid
        {
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid
        {
            Height = 40,
            Padding = new Thickness(2, 0, 140, 0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _titleBar = header;
        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerContent.Children.Add(new FontIcon
        {
            Glyph = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Label(0xAA),
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerContent.Children.Add(new TextBlock
        {
            Text = "Dev Grid Search",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = _uiFont,
            Foreground = Label(0xCC),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(headerContent);

        Grid.SetRow(header, 0);
        content.Children.Add(header);

        _queryBox = new TextBox
        {
            PlaceholderText = "Search files and folders…",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0),
            FontFamily = _uiFont,
            Foreground = new SolidColorBrush(_labelColor),
        };
        _queryBox.TextChanged += OnQueryTextChanged;
        _queryBox.KeyDown += OnQueryBoxKeyDown;
        StripTextBoxChrome(_queryBox);

        var searchBoxShell = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Solid(Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = _borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 13, 8, 13),
            Margin = new Thickness(0, 6, 0, 16),
        };
        var searchBoxGrid = new Grid { ColumnSpacing = 12 };
        searchBoxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBoxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchBoxGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = Label(0xB0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_queryBox, 1);
        searchBoxGrid.Children.Add(_queryBox);
        searchBoxShell.Child = searchBoxGrid;
        Grid.SetRow(searchBoxShell, 1);
        content.Children.Add(searchBoxShell);

        _resultsList = new ListView
        {
            ItemsSource = _results,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            ItemTemplate = CreateResultTemplate(),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _resultsList.ItemContainerStyle = CreateResultContainerStyle();
        _resultsList.Resources["ControlCornerRadius"] = new CornerRadius(14);
        _resultsList.Resources["ListViewItemBackground"] = Solid(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        _resultsList.Resources["ListViewItemBackgroundPointerOver"] = Label(0x12);
        _resultsList.Resources["ListViewItemBackgroundPressed"] = Label(0x1E);
        _resultsList.Resources["ListViewItemBackgroundSelected"] = Label(0x1E);
        _resultsList.Resources["ListViewItemBackgroundSelectedPointerOver"] = Label(0x28);
        _resultsList.Resources["ListViewItemBackgroundSelectedPressed"] = Label(0x28);
        _resultsList.Resources["ListViewItemSelectionIndicatorBrush"] = Label(0x8C);
        _resultsList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is EverythingSearchResult result)
            {
                OpenResult(result);
            }
        };
        _resultsList.DoubleTapped += (_, __) => OpenSelectedResult();
        _resultsList.RightTapped += OnResultRightTapped;

        _statusText = new TextBlock
        {
            Text = "Type to search files and folders.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            FontSize = 13,
            FontFamily = _uiFont,
            Foreground = Label(0x9E),
            IsHitTestVisible = false,
        };

        _recentHeader = new TextBlock
        {
            Text = "Recent",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = _uiFont,
            Foreground = Label(0x8C),
            Margin = new Thickness(6, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };

        var resultsRegion = new Grid();
        resultsRegion.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        resultsRegion.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_recentHeader, 0);
        Grid.SetRow(_resultsList, 1);
        Grid.SetRowSpan(_statusText, 2);
        resultsRegion.Children.Add(_recentHeader);
        resultsRegion.Children.Add(_resultsList);
        resultsRegion.Children.Add(_statusText);
        Grid.SetRow(resultsRegion, 2);
        content.Children.Add(resultsRegion);

        AddAccelerator(content, Windows.System.VirtualKey.E, () => { var r = CurrentResult(); if (r != null) { RevealResult(r); } });
        AddAccelerator(content, Windows.System.VirtualKey.C, () => { var r = CurrentResult(); if (r != null) { RecordUse(r); CopyToClipboard(r.FullPath); } });
        AddAccelerator(content, Windows.System.VirtualKey.R, () => { var r = CurrentResult(); if (r != null) { OpenInTerminal(r); } });
        content.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        var escapeAccelerator = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escapeAccelerator.Invoked += (_, args) =>
        {
            HideWindow();
            args.Handled = true;
        };
        content.KeyboardAccelerators.Add(escapeAccelerator);

        root.Children.Add(content);

        Content = root;
        Closed += (_, __) => Dispose();
        Activated += OnWindowActivated;

        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += OnSearchDebounceTick;

        ConfigureWindow();
    }

    public void FocusSearchBox()
    {
        _queryBox.Focus(FocusState.Programmatic);
        _queryBox.SelectAll();
    }

    public void ShowAndFocus()
    {
        try
        {
            AppWindow?.Show();
        }
        catch
        {
        }

        Activate();
        _queryBox.Text = string.Empty;
        FocusSearchBox();
        _ = ShowRecentsAsync();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_disposed || _contextMenuOpen || args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        // Light-dismiss: hide when another window takes focus (Spotlight-style).
        HideWindow();
    }

    private void HideWindow()
    {
        try
        {
            AppWindow?.Hide();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _searchDebounceTimer.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    private void ConfigureWindow()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            presenter?.Restore();
            if (presenter != null)
            {
                presenter.IsResizable = true;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }

            if (AppWindow?.TitleBar != null)
            {
                var tb = AppWindow.TitleBar;
                tb.ExtendsContentIntoTitleBar = true;
                tb.PreferredHeightOption = TitleBarHeightOption.Standard;
                tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 0, 0, 0);
                tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(36, 0, 0, 0);
            }

            SetTitleBar(_titleBar);

            AppWindow.Resize(new SizeInt32(790, 520));

            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var size = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                workArea.X + Math.Max((workArea.Width - size.Width) / 2, 0),
                workArea.Y + Math.Max((workArea.Height - size.Height) / 5, 24)));
        }
        catch
        {
        }
    }

    private void OnQueryTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _pendingQuery = _queryBox.Text ?? string.Empty;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async void OnSearchDebounceTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var cts = _searchCts;
        if (cts == null)
        {
            return;
        }

        await SearchAsync(_pendingQuery, cts.Token).ConfigureAwait(false);
    }

    private void OnQueryBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                RevealSelectedResult();
            }
            else
            {
                OpenSelectedResult();
            }

            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down && _results.Count > 0)
        {
            _resultsList.SelectedIndex = Math.Max(0, _resultsList.SelectedIndex);
            _resultsList.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                await ShowRecentsAsync().ConfigureAwait(false);
                return;
            }

            RunOnUi(() =>
            {
                _recentHeader.Visibility = Visibility.Collapsed;
                SetStatus("Searching…");
            });
            var response = await _searchService.SearchAsync(query, 80, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            RunOnUi(() =>
            {
                _recentHeader.Visibility = Visibility.Collapsed;
                _results.Clear();
                foreach (var result in response.Results)
                {
                    _results.Add(result);
                }

                if (_results.Count > 0)
                {
                    _resultsList.SelectedIndex = 0;
                }

                SetStatus(response.StatusMessage);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.LogError("EverythingSearchWindow: search failed.", ex);
            RunOnUi(() => SetStatus("Search failed: " + ex.Message));
        }
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text ?? string.Empty;
        _statusText.Visibility = _results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ShowRecentsAsync()
    {
        List<EverythingSearchResult> items = null;
        try
        {
            items = await Task.Run(BuildRecentResults).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("EverythingSearchWindow: failed to load recents.", ex);
        }

        RunOnUi(() =>
        {
            if (!string.IsNullOrWhiteSpace(_queryBox.Text))
            {
                return;
            }

            _results.Clear();
            if (items != null)
            {
                foreach (var item in items)
                {
                    _results.Add(item);
                }
            }

            _recentHeader.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_results.Count > 0)
            {
                _resultsList.SelectedIndex = 0;
            }

            SetStatus(_results.Count > 0 ? string.Empty : "Type to search files and folders.");
        });
    }

    private List<EverythingSearchResult> BuildRecentResults()
    {
        var list = new List<EverythingSearchResult>();
        var missing = new List<string>();

        foreach (var entry in _recentStore.GetRecents(RecentSearchStore.MaxEntries))
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.FullPath))
            {
                continue;
            }

            try
            {
                var exists = entry.IsFolder
                    ? Directory.Exists(entry.FullPath)
                    : File.Exists(entry.FullPath);
                if (!exists)
                {
                    // Stale entry: the file/folder is gone. Drop it from the store.
                    missing.Add(entry.FullPath);
                    continue;
                }

                if (entry.IsFolder)
                {
                    var di = new DirectoryInfo(entry.FullPath);
                    list.Add(new EverythingSearchResult
                    {
                        FullPath = entry.FullPath,
                        Name = string.IsNullOrWhiteSpace(entry.Name) ? di.Name : entry.Name,
                        DirectoryPath = di.Parent?.FullName ?? string.Empty,
                        IsFolder = true,
                        DateModified = di.LastWriteTime,
                    });
                }
                else
                {
                    var fi = new FileInfo(entry.FullPath);
                    list.Add(new EverythingSearchResult
                    {
                        FullPath = entry.FullPath,
                        Name = string.IsNullOrWhiteSpace(entry.Name) ? fi.Name : entry.Name,
                        DirectoryPath = fi.DirectoryName ?? string.Empty,
                        IsFolder = false,
                        SizeBytes = fi.Length,
                        DateModified = fi.LastWriteTime,
                    });
                }
            }
            catch
            {
                // Skip inaccessible entries (don't prune: could be a transient/offline path).
            }
        }

        if (missing.Count > 0)
        {
            _recentStore.RemoveMany(missing);
        }

        return list;
    }

    private void RecordUse(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

        _recentStore.Record(result.FullPath, result.Name, result.IsFolder);
        if (string.IsNullOrWhiteSpace(_queryBox.Text))
        {
            _ = ShowRecentsAsync();
        }
    }

    private void RunOnUi(Action action)
    {
        if (action == null || _disposed)
        {
            return;
        }

        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

    private void OpenSelectedResult()
    {
        if (_resultsList.SelectedItem is EverythingSearchResult result)
        {
            OpenResult(result);
        }
    }

    private void RevealSelectedResult()
    {
        if (_resultsList.SelectedItem is EverythingSearchResult result)
        {
            RevealResult(result);
        }
    }

    private void OpenResult(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

        RecordUse(result);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.FullPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"EverythingSearchWindow: failed to open '{result.FullPath}'.", ex);
        }
    }

    private void RevealResult(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

        RecordUse(result);
        try
        {
            var path = result.IsFolder ? result.FullPath : result.DirectoryPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.GetDirectoryName(result.FullPath) ?? result.FullPath;
            }

            var arguments = result.IsFolder
                ? $"\"{path}\""
                : $"/select,\"{result.FullPath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"EverythingSearchWindow: failed to reveal '{result.FullPath}'.", ex);
        }
    }

    private static DataTemplate CreateResultTemplate()
    {
        const string xaml = """
<DataTemplate
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border
        Margin="0"
        Padding="12,10"
        Background="Transparent">
    <Grid ColumnSpacing="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <Border
            Grid.Column="0"
            Width="38"
            Height="38"
            CornerRadius="12"
            Background="#142F3A3F"
            VerticalAlignment="Center">
            <FontIcon
                Glyph="{Binding IconGlyph}"
                FontFamily="Segoe MDL2 Assets"
                FontSize="18"
                Foreground="#CC2F3A3F"
                HorizontalAlignment="Center"
                VerticalAlignment="Center" />
        </Border>
        <StackPanel Grid.Column="1" Spacing="2">
            <TextBlock Text="{Binding Name}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
            <TextBlock Text="{Binding DirectoryPath}" FontSize="12" Opacity="0.68" TextTrimming="CharacterEllipsis" />
        </StackPanel>
        <StackPanel Grid.Column="2" MinWidth="120" HorizontalAlignment="Right">
            <TextBlock Text="{Binding TypeLabel}" FontSize="12" FontWeight="SemiBold" HorizontalAlignment="Right" />
            <TextBlock Text="{Binding SizeLabel}" FontSize="12" Opacity="0.66" HorizontalAlignment="Right" />
            <TextBlock Text="{Binding DateModifiedLabel}" FontSize="12" Opacity="0.66" HorizontalAlignment="Right" />
        </StackPanel>
    </Grid>
    </Border>
</DataTemplate>
""";

        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private static Style CreateResultContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(14)));
        return style;
    }

    private void OnResultRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var result = (e.OriginalSource as FrameworkElement)?.DataContext as EverythingSearchResult;
        if (result == null)
        {
            return;
        }

        _resultsList.SelectedItem = result;
        var menu = BuildResultContextMenu(result);
        _contextMenuOpen = true;
        menu.Closed += (_, __) => _contextMenuOpen = false;
        menu.ShowAt(_resultsList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition(_resultsList),
        });
        e.Handled = true;
    }

    private void AddAccelerator(UIElement target, Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
        };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        target.KeyboardAccelerators.Add(accelerator);
    }

    private EverythingSearchResult CurrentResult()
    {
        return (_resultsList.SelectedItem as EverythingSearchResult)
            ?? (_results.Count > 0 ? _results[0] : null);
    }

    private MenuFlyout BuildResultContextMenu(EverythingSearchResult result)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(CreateMenuItem("Open", "\uE8E5", null, () => OpenResult(result)));
        if (!result.IsFolder)
        {
            menu.Items.Add(CreateMenuItem("Open with…", "\uE7AC", null, () => OpenWith(result)));
        }

        menu.Items.Add(CreateMenuItem("Show in folder", "\uE838", "Ctrl+Shift+E", () => RevealResult(result)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("Copy path", "\uE8C8", "Ctrl+Shift+C", () => { RecordUse(result); CopyToClipboard(result.FullPath); }));
        menu.Items.Add(CreateMenuItem("Copy name", "\uE8C8", null, () => { RecordUse(result); CopyToClipboard(result.Name); }));
        menu.Items.Add(CreateMenuItem("Open in terminal", "\uE756", "Ctrl+Shift+R", () => OpenInTerminal(result)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("Properties", "\uE946", null, () => ShowProperties(result)));

        return menu;
    }

    private MenuFlyoutItem CreateMenuItem(string text, string glyph, string acceleratorText, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            FontFamily = _uiFont,
            Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets") },
        };
        if (!string.IsNullOrEmpty(acceleratorText))
        {
            item.KeyboardAcceleratorTextOverride = acceleratorText;
        }

        item.Click += (_, __) => action();
        return item;
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("EverythingSearchWindow: copy to clipboard failed.", ex);
        }
    }

    private void OpenWith(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

        RecordUse(result);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL {result.FullPath}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"EverythingSearchWindow: open-with failed for '{result.FullPath}'.", ex);
        }
    }

    private void OpenInTerminal(EverythingSearchResult result)
    {
        if (result == null)
        {
            return;
        }

        RecordUse(result);
        var directory = result.IsFolder ? result.FullPath : result.DirectoryPath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.GetDirectoryName(result.FullPath) ?? result.FullPath;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{directory}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = directory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"EverythingSearchWindow: open-in-terminal failed for '{directory}'.", ex);
            }
        }
    }

    private void ShowProperties(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

        RecordUse(result);
        try
        {
            var info = new SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>();
            info.lpVerb = "properties";
            info.lpFile = result.FullPath;
            info.nShow = 1;
            info.fMask = SeeMaskInvokeIdList;
            info.hwnd = this.GetWindowHandle();
            if (!ShellExecuteEx(ref info))
            {
                AppLogger.LogWarning($"EverythingSearchWindow: properties dialog failed for '{result.FullPath}'.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"EverythingSearchWindow: properties failed for '{result.FullPath}'.", ex);
        }
    }

    private const uint SeeMaskInvokeIdList = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    private static Brush ReadBrush(ResourceDictionary resources, string key)
    {
        if (resources != null && !string.IsNullOrWhiteSpace(key) &&
            resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return null;
    }

    private static FontFamily ReadFont(ResourceDictionary resources, string key)
    {
        if (resources != null && resources.TryGetValue(key, out var value) &&
            value is FontFamily font && !string.IsNullOrWhiteSpace(font.Source))
        {
            return new FontFamily(font.Source);
        }

        return null;
    }

    private static Brush CloneBrush(Brush brush)
    {
        if (brush is SolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color);
        }

        if (brush is RadialGradientBrush radial)
        {
            var clone = new RadialGradientBrush
            {
                Center = radial.Center,
                GradientOrigin = radial.GradientOrigin,
                RadiusX = radial.RadiusX,
                RadiusY = radial.RadiusY,
                SpreadMethod = radial.SpreadMethod,
            };
            foreach (var stop in radial.GradientStops)
            {
                clone.GradientStops.Add(new GradientStop { Color = stop.Color, Offset = stop.Offset });
            }

            return clone;
        }

        if (brush is LinearGradientBrush linear)
        {
            var clone = new LinearGradientBrush
            {
                StartPoint = linear.StartPoint,
                EndPoint = linear.EndPoint,
            };
            foreach (var stop in linear.GradientStops)
            {
                clone.GradientStops.Add(new GradientStop { Color = stop.Color, Offset = stop.Offset });
            }

            return clone;
        }

        return null;
    }

    private static Color ResolveBrushColor(Brush brush, Color fallback)
    {
        if (brush is SolidColorBrush solid)
        {
            return solid.Color;
        }

        if (brush is GradientBrush gradient && gradient.GradientStops != null && gradient.GradientStops.Count > 0)
        {
            return gradient.GradientStops[0].Color;
        }

        return fallback;
    }

    private static Brush CreateDefaultSurfaceBrush()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 1.02),
            GradientOrigin = new Windows.Foundation.Point(0.5, 1.02),
            RadiusX = 0.95,
            RadiusY = 1.25,
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xEA, 0xFC, 0xF7, 0xF1), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xDD, 0xEE, 0xE6, 0xDB), Offset = 0.58 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xCF, 0xD9, 0xCE, 0xC0), Offset = 1.0 });
        return brush;
    }

    private static void StripTextBoxChrome(TextBox box)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrush"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
    }
}
