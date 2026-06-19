// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
using Windows.Graphics;
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

    public EverythingSearchWindow(EverythingSearchService searchService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));

        Title = "Dev Grid Search";
        IsTitleBarVisible = true;
        ExtendsContentIntoTitleBar = true;
        MinWidth = 620;
        MinHeight = 420;
        Width = 820;
        Height = 560;

        var root = new Grid
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF7, 0xFC, 0xF7, 0xF1)),
            Padding = new Thickness(22, 18, 22, 18),
        };

        var content = new Grid
        {
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid
        {
            Height = 56,
            Padding = new Thickness(0, 0, 116, 0),
            ColumnSpacing = 12,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _titleBar = header;
        var titleStack = new StackPanel { Spacing = 2 };
        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerContent.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x4F, 0x8B, 0xFF), Offset = 0 },
                    new GradientStop { Color = ColorHelper.FromArgb(0xFF, 0x7C, 0x4D, 0xFF), Offset = 1 },
                },
            },
            Child = new FontIcon
            {
                Glyph = "\uE721",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Dev Grid Search",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x2F, 0x3A, 0x3F)),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Powered by Everything",
            FontSize = 11,
            Opacity = 0.62,
        });
        headerContent.Children.Add(titleStack);
        header.Children.Add(headerContent);

        Grid.SetRow(header, 0);
        content.Children.Add(header);

        _queryBox = new TextBox
        {
            PlaceholderText = "Type a filename, path, extension, or Everything query...",
            FontSize = 16,
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 8, 0, 8),
        };
        _queryBox.TextChanged += OnQueryTextChanged;
        _queryBox.KeyDown += OnQueryBoxKeyDown;
        var searchBoxShell = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 2, 10, 2),
        };
        var searchBoxGrid = new Grid { ColumnSpacing = 10 };
        searchBoxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBoxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchBoxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBoxGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 17,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_queryBox, 1);
        searchBoxGrid.Children.Add(_queryBox);
        var clearButton = new Button
        {
            Content = "\uE711",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(clearButton, "Clear");
        clearButton.Click += (_, __) => _queryBox.Text = string.Empty;
        Grid.SetColumn(clearButton, 2);
        searchBoxGrid.Children.Add(clearButton);
        searchBoxShell.Child = searchBoxGrid;
        Grid.SetRow(searchBoxShell, 1);
        searchBoxShell.Margin = new Thickness(0, 0, 0, 14);
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
        _resultsList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is EverythingSearchResult result)
            {
                OpenResult(result);
            }
        };
        _resultsList.DoubleTapped += (_, __) => OpenSelectedResult();
        Grid.SetRow(_resultsList, 2);
        content.Children.Add(_resultsList);

        var footer = new Grid { ColumnSpacing = 10, MinHeight = 40 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _statusText = new TextBlock
        {
            Text = "Type to search files and folders.",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xCC, 0x2F, 0x3A, 0x3F)),
        };
        Grid.SetColumn(_statusText, 0);
        footer.Children.Add(_statusText);

        var openButton = new Button
        {
            Content = "Open",
            MinWidth = 86,
            Height = 36,
            CornerRadius = new CornerRadius(18),
        };
        openButton.Click += (_, __) => OpenSelectedResult();
        Grid.SetColumn(openButton, 1);
        footer.Children.Add(openButton);

        var revealButton = new Button
        {
            Content = "Show in folder",
            MinWidth = 128,
            Height = 36,
            CornerRadius = new CornerRadius(18),
        };
        revealButton.Click += (_, __) => RevealSelectedResult();
        Grid.SetColumn(revealButton, 2);
        footer.Children.Add(revealButton);

        Grid.SetRow(footer, 3);
        footer.Margin = new Thickness(0, 12, 0, 0);
        content.Children.Add(footer);

        root.Children.Add(content);

        Content = root;
        Closed += (_, __) => Dispose();

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
                RunOnUi(() =>
                {
                    _results.Clear();
                    _statusText.Text = "Type to search files and folders.";
                });
                return;
            }

            RunOnUi(() => _statusText.Text = "Searching...");
            var response = await _searchService.SearchAsync(query, 80, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            RunOnUi(() =>
            {
                _results.Clear();
                foreach (var result in response.Results)
                {
                    _results.Add(result);
                }

                _statusText.Text = response.StatusMessage;
                if (_results.Count > 0)
                {
                    _resultsList.SelectedIndex = 0;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.LogError("EverythingSearchWindow: search failed.", ex);
            RunOnUi(() => _statusText.Text = "Search failed: " + ex.Message);
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

    private static void OpenResult(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

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

    private static void RevealResult(EverythingSearchResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            return;
        }

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
        Margin="0,4"
        Padding="12,10"
        CornerRadius="16"
        Background="#0A000000"
        BorderBrush="#12FFFFFF"
        BorderThickness="1">
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
            Background="#12007A87"
            VerticalAlignment="Center">
            <FontIcon
                Glyph="{Binding IconGlyph}"
                FontFamily="Segoe MDL2 Assets"
                FontSize="18"
                Foreground="#FF007A87"
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
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 0d));
        return style;
    }
}
