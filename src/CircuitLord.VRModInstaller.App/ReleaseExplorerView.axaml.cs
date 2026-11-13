using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Core.Manifest;
using CircuitLord.VRModInstaller.Core.Releases;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Full release browser over the validated backend catalogue. It can inspect
/// and compare releases, but only commits the exact backend token when the
/// user explicitly chooses "Use this release".
/// </summary>
public partial class ReleaseExplorerView : UserControl
{
    private sealed record ReleaseEntry(
        VersionOption Option,
        ReleaseBrowserItem Item,
        string GroupTitle,
        string GroupHint);

    private InstallerViewModel? _vm;
    private List<ReleaseEntry> _entries = new();
    private List<ReleaseEntry> _visibleEntries = new();
    private readonly Dictionary<string, Border> _cards = new(StringComparer.Ordinal);
    private string _filter = "all";
    private string _sort = "order";
    private string _searchText = string.Empty;
    private string? _inspectedToken;
    private bool _loading = true;
    private bool _updatingCompare;
    private bool _languageSubscribed;
    private bool _viewModelSubscribed;
    private string _catalogueSignature = string.Empty;
    private bool _refreshing;
    private bool _compactLayout;
    private string? _refreshError;
    private bool _embedded;
    private double _savedListOffset;
    private double _savedDetailsOffset;
    private double _savedCompareOffset;

    public ReleaseExplorerView()
    {
        InitializeComponent();
        _loading = false;
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToLanguage();
            if (_vm is not null)
            {
                SubscribeToViewModel(_vm);
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnsubscribeFromLanguage();
            UnsubscribeFromViewModel();
        };
    }

    public event EventHandler? Closed;

    public event EventHandler? OverviewRequested;

    public void Open(InstallerViewModel vm, bool embedded = false)
    {
        _embedded = embedded;
        ReleaseBackButton.IsVisible = !embedded;
        ReleaseHeaderTitle.Margin = new Thickness(embedded ? 0 : 14, 0, 0, 0);
        UnsubscribeFromViewModel();
        _vm = vm;
        _loading = true;
        ReleaseSearchBox.Text = _searchText;
        ReleaseSortCombo.SelectedIndex = SortIndex(_sort);
        _loading = false;

        SubscribeToViewModel(vm);
        if (!vm.Busy)
        {
            vm.RefreshLocalState();
        }
        ReloadEntries();
        if (string.IsNullOrWhiteSpace(_inspectedToken)
            || _entries.All(entry => !MatchesToken(entry, _inspectedToken)))
        {
            _inspectedToken = vm.SelectedVersion?.Token ?? _entries.FirstOrDefault()?.Option.Token;
        }

        IsVisible = true;
        ShowList();
        UpdateHeader();
        RenderList();
        ApplyResponsiveLayout(Bounds.Width);
        _savedListOffset = 0;
        _savedDetailsOffset = 0;
        _savedCompareOffset = 0;
        ReleaseListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsVisible)
                {
                    return;
                }
                Focus();
                if (_inspectedToken is not null && _cards.TryGetValue(_inspectedToken, out var card))
                {
                    card.Focus();
                }
                else
                {
                    ReleaseSearchBox.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Restores an already-open tab without resetting its search, filters,
    /// inspected release or list position. The catalogue is reloaded only when
    /// the shared catalogue changed while the tab was hidden.
    /// </summary>
    public void Activate(InstallerViewModel vm)
    {
        _vm = vm;
        SubscribeToViewModel(vm);
        IsVisible = true;

        if (CatalogueSignature() == _catalogueSignature)
        {
            UpdateHeader();
            UpdateDetails();
            return;
        }

        var listOffset = ReleaseListScroll.Offset.Y;
        ReloadEntries();
        UpdateHeader();
        if (ReleaseCompareScroll.IsVisible)
        {
            if (_entries.Count < 2)
            {
                ShowList();
                RenderList();
            }
            else
            {
                BuildCompareOptions();
                RenderComparison();
            }
        }
        else
        {
            RenderList();
            UpdateDetails();
            RestoreListOffset(listOffset);
        }
    }

    /// <summary>Captures nested scroll positions before this tab is hidden.</summary>
    public void SaveScrollState()
    {
        _savedListOffset = ReleaseListScroll.Offset.Y;
        _savedDetailsOffset = ReleaseDetailsScroll.Offset.Y;
        _savedCompareOffset = ReleaseCompareScroll.Offset.Y;
    }

    /// <summary>Restores nested scroll positions after layout has measured the tab.</summary>
    public void RestoreScrollState()
    {
        void Restore()
        {
            if (!IsVisible)
            {
                return;
            }
            RestoreScrollOffset(ReleaseListScroll, _savedListOffset);
            RestoreScrollOffset(ReleaseDetailsScroll, _savedDetailsOffset);
            if (ReleaseCompareScroll.IsVisible)
            {
                RestoreScrollOffset(ReleaseCompareScroll, _savedCompareOffset);
            }
        }

        // The first pass runs as soon as the hidden ScrollViewers have been
        // measured; the second settles a viewport whose scrollbar changed the
        // available height during that first pass.
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
    }

    private void RestoreListOffset(double offset)
    {
        Dispatcher.UIThread.Post(
            () => RestoreScrollOffset(ReleaseListScroll, offset),
            DispatcherPriority.Loaded);
    }

    private static void RestoreScrollOffset(ScrollViewer scroll, double offset)
    {
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.SetCurrentValue(
            ScrollViewer.OffsetProperty,
            new Vector(scroll.Offset.X, Math.Clamp(offset, 0, max)));
    }

    public bool HandleEscape()
    {
        if (!IsVisible)
        {
            return false;
        }
        if (ReleaseCompareScroll.IsVisible)
        {
            ShowList();
            RenderList();
            return true;
        }
        if (_embedded)
        {
            OverviewRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        CloseExplorer();
        return true;
    }

    public void RefreshAppearance()
    {
        if (!IsVisible)
        {
            return;
        }
        SaveScrollState();
        UpdateHeader();
        if (ReleaseCompareScroll.IsVisible)
        {
            RenderComparison();
        }
        else
        {
            RenderList();
            UpdateDetails();
        }
        RestoreScrollState();
    }

    private void SubscribeToLanguage()
    {
        if (_languageSubscribed)
        {
            return;
        }
        Loc.LanguageChanged += LanguageChanged;
        _languageSubscribed = true;
    }

    private void UnsubscribeFromLanguage()
    {
        if (!_languageSubscribed)
        {
            return;
        }
        Loc.LanguageChanged -= LanguageChanged;
        _languageSubscribed = false;
    }

    private void SubscribeToViewModel(InstallerViewModel vm)
    {
        UnsubscribeFromViewModel();
        vm.Changed += ViewModelChanged;
        _viewModelSubscribed = true;
    }

    private void UnsubscribeFromViewModel()
    {
        if (!_viewModelSubscribed)
        {
            return;
        }
        if (_vm is not null)
        {
            _vm.Changed -= ViewModelChanged;
        }
        _viewModelSubscribed = false;
    }

    private void ViewModelChanged(object? sender, EventArgs e)
    {
        if (!IsVisible || _vm is null || CatalogueSignature() == _catalogueSignature)
        {
            return;
        }

        // A background catalogue refresh may add or remove choices. Refresh
        // the browse data without disturbing a comparison, the search/filter
        // state or the reader's position; UpdateDetails validates the token.
        SaveScrollState();
        ReloadEntries();
        UpdateHeader();
        if (ReleaseCompareScroll.IsVisible)
        {
            if (_entries.Count < 2)
            {
                ShowList();
                RenderList();
            }
            else
            {
                BuildCompareOptions();
                RenderComparison();
            }
        }
        else
        {
            RenderList();
            UpdateDetails();
        }
        RestoreScrollState();
    }

    private string CatalogueSignature()
    {
        if (_vm is null)
        {
            return string.Empty;
        }
        return (_vm.CatalogueRetrievedAt?.UtcTicks ?? 0).ToString()
            + "|" + _vm.CatalogueIsCached
            + "|" + _vm.CatalogueDiagnostic
            + "|" + string.Join(",", _vm.Versions.Select(version => version.Token));
    }

    private void LanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || _vm is null)
            {
                return;
            }
            SaveScrollState();
            ReloadEntries();
            UpdateHeader();
            if (ReleaseCompareScroll.IsVisible)
            {
                BuildCompareOptions();
                RenderComparison();
            }
            else
            {
                RenderList();
                UpdateDetails();
            }
            RestoreScrollState();
        });

    private void Explorer_SizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width > 0 && width < 900;
        if (compact == _compactLayout && ExplorerBody.ColumnDefinitions.Count > 0)
        {
            return;
        }
        _compactLayout = compact;
        if (compact)
        {
            ReleaseListHeader.ColumnDefinitions = new ColumnDefinitions("Auto,*");
            ReleaseListHeader.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(ReleaseHeaderTitle, 1);
            Grid.SetRow(ReleaseHeaderTitle, 0);
            Grid.SetColumn(ReleaseHeaderStatus, 1);
            Grid.SetRow(ReleaseHeaderStatus, 1);
            ReleaseHeaderStatus.Margin = new Thickness(14, 4, 0, 0);
            ReleaseToolbarActions.ColumnDefinitions = new ColumnDefinitions("*");
            ReleaseToolbarActions.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(ReleaseFilters, 0);
            Grid.SetRow(ReleaseFilters, 0);
            Grid.SetColumn(OpenCompareButton, 0);
            Grid.SetRow(OpenCompareButton, 1);
            OpenCompareButton.HorizontalAlignment = HorizontalAlignment.Right;
            OpenCompareButton.Margin = new Thickness(0, 6, 0, 0);
            ExplorerBody.ColumnDefinitions = new ColumnDefinitions("*");
            ExplorerBody.RowDefinitions = new RowDefinitions("3*,2*");
            Grid.SetColumn(ReleaseDetailsPane, 0);
            Grid.SetRow(ReleaseDetailsPane, 1);
            Grid.SetColumnSpan(ReleaseDetailsPane, 1);
            Grid.SetColumn(ReleaseCompareScroll, 0);
            Grid.SetRow(ReleaseCompareScroll, 0);
            Grid.SetRowSpan(ReleaseCompareScroll, 2);
            Grid.SetColumnSpan(ReleaseCompareScroll, 1);
            ReleaseDetailsPane.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            ReleaseListHeader.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
            ReleaseListHeader.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(ReleaseHeaderTitle, 1);
            Grid.SetRow(ReleaseHeaderTitle, 0);
            Grid.SetColumn(ReleaseHeaderStatus, 2);
            Grid.SetRow(ReleaseHeaderStatus, 0);
            ReleaseHeaderStatus.Margin = default;
            ReleaseToolbarActions.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            ReleaseToolbarActions.RowDefinitions = new RowDefinitions("Auto");
            Grid.SetColumn(ReleaseFilters, 0);
            Grid.SetRow(ReleaseFilters, 0);
            Grid.SetColumn(OpenCompareButton, 1);
            Grid.SetRow(OpenCompareButton, 0);
            OpenCompareButton.HorizontalAlignment = HorizontalAlignment.Right;
            OpenCompareButton.Margin = default;
            ExplorerBody.ColumnDefinitions = new ColumnDefinitions("*,360");
            ExplorerBody.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(ReleaseDetailsPane, 1);
            Grid.SetRow(ReleaseDetailsPane, 0);
            Grid.SetColumnSpan(ReleaseDetailsPane, 1);
            Grid.SetColumn(ReleaseCompareScroll, 0);
            Grid.SetRow(ReleaseCompareScroll, 0);
            Grid.SetRowSpan(ReleaseCompareScroll, 1);
            Grid.SetColumnSpan(ReleaseCompareScroll, 2);
            ReleaseDetailsPane.Margin = default;
        }
    }

    private void CloseExplorer_Click(object? sender, RoutedEventArgs e) => CloseExplorer();

    private void CloseExplorer()
    {
        HelpTip.CloseOpen();
        _searchText = ReleaseSearchBox.Text ?? string.Empty;
        if (_embedded)
        {
            OverviewRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        UnsubscribeFromViewModel();
        IsVisible = false;
        _vm = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Explorer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HandleEscape())
        {
            e.Handled = true;
        }
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        _searchText = ReleaseSearchBox.Text ?? string.Empty;
        ClearSearchButton.IsVisible = _searchText.Length > 0;
        if (!_loading)
        {
            RenderList();
        }
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        ReleaseSearchBox.Text = string.Empty;
        ReleaseSearchBox.Focus();
    }

    private void Sort_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _sort = (ReleaseSortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "order";
        RenderList();
    }

    private void Filter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter })
        {
            return;
        }
        _filter = filter;
        RenderList();
    }

    private void ClearFilters_Click(object? sender, RoutedEventArgs e)
    {
        _filter = "all";
        RenderList();
    }

    private void BackToReleases_Click(object? sender, RoutedEventArgs e)
    {
        ShowList();
        RenderList();
    }

    private async void RefreshCatalogue_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _refreshing || _vm.Busy)
        {
            return;
        }

        _refreshing = true;
        _refreshError = null;
        UseReleaseButton.IsEnabled = false;
        UpdateHeader();
        try
        {
            await _vm.RefreshCatalogueAsync();
            ReloadEntries();
            RenderList();
            UpdateDetails();
        }
        catch (Exception)
        {
            _refreshError = Loc.Get("catalogueRefreshFailed");
        }
        finally
        {
            _refreshing = false;
            UpdateHeader();
            UseReleaseButton.IsEnabled = _entries.Any(entry => MatchesToken(entry, _inspectedToken));
        }
    }

    private void ShowList()
    {
        ReleaseListHeader.IsVisible = true;
        ReleaseCompareHeader.IsVisible = false;
        ReleaseToolbar.IsVisible = true;
        ReleaseListPane.IsVisible = true;
        ReleaseDetailsPane.IsVisible = true;
        ReleaseCompareScroll.IsVisible = false;
    }

    private void OpenCompare_Click(object? sender, RoutedEventArgs e) => OpenComparison();

    private void OpenComparison()
    {
        if (_entries.Count < 2)
        {
            return;
        }

        ReleaseListHeader.IsVisible = false;
        ReleaseCompareHeader.IsVisible = true;
        ReleaseToolbar.IsVisible = false;
        ReleaseListPane.IsVisible = false;
        ReleaseDetailsPane.IsVisible = false;
        ReleaseCompareScroll.IsVisible = true;
        BuildCompareOptions();
        ReleaseCompareScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
    }

    private void ReloadEntries()
    {
        _entries = new List<ReleaseEntry>();
        if (_vm is null)
        {
            _catalogueSignature = string.Empty;
            return;
        }

        foreach (var group in _vm.VersionGroups)
        {
            foreach (var option in group.Options)
            {
                if (option.CatalogueEntry is null)
                {
                    continue;
                }

                var description = _vm.GetReleaseDescription(option);
                var manifestDescription = _vm.GetReleaseManifestDescription(option);
                var item = new ReleaseBrowserItem
                {
                    Entry = option.CatalogueEntry,
                    ChannelKey = option.GroupKey,
                    ChannelLabel = group.Title,
                    Description = description,
                    ManifestDescription = manifestDescription,
                    Changelog = _vm.GetReleaseChangelogText(option),
                    LastUpdated = _vm.GetReleaseLastUpdated(option),
                    IsInstalled = option.IsInstalled,
                    IsSelected = option.IsSelected,
                };
                _entries.Add(new ReleaseEntry(option, item, group.Title, group.Hint));
            }
        }
        _catalogueSignature = CatalogueSignature();
    }

    private void RenderList()
    {
        SyncFilterClasses();
        ReleaseGroupPanel.Children.Clear();
        _cards.Clear();
        _visibleEntries = new List<ReleaseEntry>();

        var filtered = ReleaseBrowser.Filter(
            _entries.Select(entry => entry.Item),
            CurrentFilter,
            ReleaseSearchBox.Text);
        var sorted = ReleaseBrowser.Sort(filtered, CurrentSort);
        var byToken = _entries.ToDictionary(entry => entry.Item.Token, StringComparer.Ordinal);
        _visibleEntries = sorted
            .Where(item => byToken.ContainsKey(item.Token))
            .Select(item => byToken[item.Token])
            .ToList();

        foreach (var group in OrderGroups())
        {
            var header = new StackPanel { Spacing = 2, Margin = new Thickness(2, 0, 2, 2) };
            header.Children.Add(new TextBlock
            {
                Text = group.First().GroupTitle,
                Classes = { "release-group-title" },
            });
            header.Children.Add(new TextBlock
            {
                Text = group.First().GroupHint,
                Classes = { "small" },
                TextWrapping = TextWrapping.Wrap,
            });

            var groupPanel = new StackPanel { Spacing = 7 };
            groupPanel.Children.Add(header);
            foreach (var entry in group)
            {
                var card = BuildCard(entry);
                groupPanel.Children.Add(card);
                _cards[entry.Option.Token] = card;
            }
            ReleaseGroupPanel.Children.Add(groupPanel);
        }

        var empty = _visibleEntries.Count == 0;
        ReleaseListScroll.IsVisible = !empty;
        ReleaseEmptyState.IsVisible = empty;
        ReleaseEmptyText.Text = empty
            ? _entries.Count > 0
                ? Loc.Get("noReleases")
                : _vm?.CatalogueRetrievedAt is null
                    ? Loc.Get("catalogueLoading")
                    : Loc.Get("catalogueNoVersions")
            : string.Empty;
        OpenCompareButton.IsEnabled = _entries.Count >= 2;
        ReleaseListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
        UpdateDetails();
    }

    private Border BuildCard(ReleaseEntry entry)
    {
        var card = new Border
        {
            Focusable = true,
            IsTabStop = true,
            Tag = entry.Option.Token,
            Classes = { "release-card" },
        };
        if (MatchesToken(entry, _inspectedToken))
        {
            card.Classes.Add("inspected");
        }
        AutomationProperties.SetName(card, AccessibleDescription(entry));
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
            {
                card.Focus();
                Inspect(entry);
            }
        };
        card.KeyDown += Card_KeyDown;

        var stack = new StackPanel { Spacing = 6 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock
        {
            Text = entry.Option.DisplayLabel,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        heading.Children.Add(new TextBlock
        {
            Text = entry.Option.VersionText,
            Classes = { "small" },
        });
        top.Children.Add(heading);

        var badges = BuildBadges(entry);
        Grid.SetColumn(badges, 1);
        top.Children.Add(badges);
        stack.Children.Add(top);

        stack.Children.Add(new TextBlock
        {
            Text = Shorten(entry.Item.Description ?? Loc.Get("notAvailable"), 180),
            Classes = { "small" },
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = entry.GroupTitle + " · " + entry.GroupHint,
            Classes = { "small" },
            TextWrapping = TextWrapping.Wrap,
        });

        card.Child = stack;
        return card;
    }

    private static StackPanel BuildBadges(ReleaseEntry entry)
    {
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var channel = entry.Option.GroupKey switch
        {
            "stable" => Loc.Get("stableLabel"),
            "beta" => Loc.Get("betaReleases"),
            "pinned" => Loc.Get("pinnedFilter"),
            _ => entry.GroupTitle,
        };
        AddBadge(badges, channel, "kind-pill", "kind-pill-text");
        if (entry.Item.IsInstalled)
        {
            AddBadge(badges, Loc.Get("installed"), "status-pill", "status-pill-text");
        }
        if (entry.Item.IsSelected)
        {
            AddBadge(badges, Loc.Get("selected"), "accent-pill", "pill-text");
        }
        if (entry.Option.IsUpToDate)
        {
            AddBadge(badges, Loc.Get("upToDateShort"), "status-pill", "status-pill-text");
        }
        return badges;
    }

    private static void AddBadge(StackPanel parent, string text, string borderClass, string textClass)
    {
        var border = new Border { Classes = { borderClass } };
        var label = new TextBlock { Text = text, Classes = { textClass } };
        border.Child = label;
        parent.Children.Add(border);
    }

    private void Inspect(ReleaseEntry entry)
    {
        _inspectedToken = entry.Option.Token;
        UpdateCardClasses();
        UpdateDetails();
        ReleaseDetailsScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
    }

    private void UpdateCardClasses()
    {
        foreach (var (token, card) in _cards)
        {
            if (string.Equals(token, _inspectedToken, StringComparison.Ordinal))
            {
                if (!card.Classes.Contains("inspected"))
                {
                    card.Classes.Add("inspected");
                }
            }
            else
            {
                card.Classes.Remove("inspected");
            }
        }
    }

    private void Card_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Border { Tag: string token })
        {
            return;
        }
        if (e.Key is Key.Enter or Key.Space)
        {
            var entry = _entries.FirstOrDefault(item => item.Option.Token == token);
            if (entry is not null)
            {
                Inspect(entry);
            }
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down)
        {
            MoveCardFocus(token, e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    private void MoveCardFocus(string token, int direction)
    {
        var index = _visibleEntries.FindIndex(entry => entry.Option.Token == token);
        if (index < 0)
        {
            return;
        }
        var target = Math.Clamp(index + direction, 0, _visibleEntries.Count - 1);
        if (_cards.TryGetValue(_visibleEntries[target].Option.Token, out var card))
        {
            card.Focus();
        }
    }

    private void UpdateDetails()
    {
        if (_vm is null)
        {
            return;
        }

        var entry = _entries.FirstOrDefault(item => MatchesToken(item, _inspectedToken));
        if (entry is null)
        {
            DetailsContent.IsVisible = false;
            UseReleaseButton.IsEnabled = false;
            DetailsEmptyState.IsVisible = true;
            DetailsEmptyState.Text = _inspectedToken is null
                ? Loc.Get("noReleaseSelected")
                : Loc.Get("releaseGone");
            return;
        }

        DetailsEmptyState.IsVisible = false;
        DetailsContent.IsVisible = true;
        DetailsName.Text = entry.Option.DisplayLabel;
        DetailsVersion.Text = entry.Option.VersionText;
        DetailsDescription.Text = entry.Item.Description ?? Loc.Get("notAvailable");
        DetailsChangelog.Text = entry.Item.Changelog ?? Loc.Get("changelogUnavailable");
        DetailsChannel.Text = entry.GroupTitle + " · " + entry.GroupHint;
        DetailsLastUpdated.Text = entry.Item.LastUpdated ?? Loc.Get("notAvailable");
        DetailsToken.Text = entry.Option.Token;
        DetailsAvailability.Text = AvailabilityText(entry);
        DetailsCatalogue.Text = CatalogueText(entry);
        DetailsManifest.Text = ManifestText(entry);
        DetailsInstalled.Text = entry.Item.IsInstalled ? Loc.Get("installed") : Loc.Get("notInstalled");
        DetailsSelected.Text = entry.Item.IsSelected ? Loc.Get("selected") : Loc.Get("notSelected");
        DetailsError.IsVisible = false;
        UseReleaseButton.IsEnabled = !_refreshing && !_vm.Busy;
        DetailsBadges.Children.Clear();
        var badges = BuildBadges(entry);
        while (badges.Children.Count > 0)
        {
            var child = badges.Children[0];
            badges.Children.RemoveAt(0);
            DetailsBadges.Children.Add(child);
        }
    }

    private void UseRelease_Click(object? sender, RoutedEventArgs e)
    {
        var entry = _entries.FirstOrDefault(item => MatchesToken(item, _inspectedToken));
        UseRelease(entry);
    }

    private void UseRelease(ReleaseEntry? entry)
    {
        if (_vm is null || _refreshing || _vm.Busy)
        {
            return;
        }
        if (entry is null)
        {
            ShowSelectionError(Loc.Get("releaseGone"));
            return;
        }
        if (_vm.Versions.All(version => !string.Equals(version.Token, entry.Option.Token, StringComparison.Ordinal))
            || !_vm.SelectReleaseToken(entry.Option.Token))
        {
            ShowSelectionError(Loc.Get("releaseGone"));
            return;
        }
        _inspectedToken = entry.Option.Token;
        CloseExplorer();
    }

    private void ShowSelectionError(string message)
    {
        if (ReleaseCompareScroll.IsVisible)
        {
            ShowList();
            RenderList();
        }
        ShowDetailsError(message);
    }

    private void ShowDetailsError(string message)
    {
        ReleaseDetailsPane.IsVisible = true;
        DetailsContent.IsVisible = true;
        UseReleaseButton.IsEnabled = false;
        DetailsEmptyState.IsVisible = false;
        DetailsError.Text = message;
        DetailsError.IsVisible = true;
    }

    private void UpdateHeader()
    {
        if (_vm is null)
        {
            return;
        }

        var count = _entries.Count;
        var hasCatalogue = _vm.CatalogueRetrievedAt is not null;
        var source = !hasCatalogue
            ? Loc.Get("catalogueLoading")
            : _vm.CatalogueIsCached
                ? Loc.Get("catalogueCached")
                : Loc.Get("catalogueLive");
        CatalogueSummaryText.Text = Loc.Format("catalogueSummary", source, count);
        CatalogueSourceText.Text = source;
        CatalogueSourceBadge.Classes.Clear();
        CatalogueSourceText.Classes.Clear();
        if (!hasCatalogue || _vm.CatalogueIsCached)
        {
            CatalogueSourceBadge.Classes.Add("status-pill");
            CatalogueSourceBadge.Classes.Add("running");
            CatalogueSourceText.Classes.Add("status-pill-text");
            CatalogueSourceText.Classes.Add("running");
        }
        else
        {
            CatalogueSourceBadge.Classes.Add("status-pill");
            CatalogueSourceText.Classes.Add("status-pill-text");
        }

        var retrieved = _vm.CatalogueRetrievedAt;
        CatalogueTimestampText.Text = retrieved is null
            ? Loc.Get("notAvailable")
            : Loc.Format("lastRefreshed", retrieved.Value.ToLocalTime().ToString("g"));

        var warning = _refreshError
            ?? (_vm.CatalogueIsCached ? Loc.Get("cachedNotGuaranteed") : _vm.CatalogueDiagnostic);
        CatalogueWarningText.Text = warning ?? string.Empty;
        CatalogueWarningText.IsVisible = !string.IsNullOrWhiteSpace(warning);
        RefreshCatalogueButton.IsEnabled = !_refreshing && !_vm.Busy;
        RefreshCatalogueButton.Content = _refreshing ? Loc.Get("catalogueLoading") : Loc.Get("refresh");
    }

    private string AvailabilityText(ReleaseEntry entry)
    {
        var source = entry.Item.IsCached ? Loc.Get("catalogueCached") : Loc.Get("catalogueLive");
        return entry.Item.IsCached
            ? source + " · " + Loc.Get("cachedNotGuaranteed")
            : source;
    }

    private string CatalogueText(ReleaseEntry entry)
        => entry.Item.IsCached ? Loc.Get("catalogueCached") : Loc.Get("catalogueLive");

    private string ManifestText(ReleaseEntry entry)
    {
        if (_vm is null)
        {
            return Loc.Get("notAvailable");
        }
        var source = _vm.ManifestIsCached ? Loc.Get("cachedData") : Loc.Get("liveData");
        var text = _vm.ManifestWarning is null
            ? source
            : source + " · " + _vm.ManifestWarning;
        return string.IsNullOrWhiteSpace(entry.Item.ManifestDescription)
            || string.Equals(entry.Item.ManifestDescription, entry.Item.Description, StringComparison.Ordinal)
            ? text
            : text + Environment.NewLine + entry.Item.ManifestDescription;
    }

    private void BuildCompareOptions()
    {
        var previousLeft = (CompareLeftCombo.SelectedItem as VersionOption)?.Token;
        var previousRight = (CompareRightCombo.SelectedItem as VersionOption)?.Token;
        var options = _entries.Select(entry => entry.Option).ToList();
        if (options.Count < 2)
        {
            return;
        }

        var left = options.FirstOrDefault(option => option.Token == previousLeft)
            ?? options.FirstOrDefault(option => option.IsSelected)
            ?? options.FirstOrDefault(option => option.Token == _inspectedToken)
            ?? options[0];
        var right = options.FirstOrDefault(option =>
                option.Token == previousRight
                && option.Token != left.Token)
            ?? options.FirstOrDefault(option =>
                option.Token != left.Token
                && (left.GroupKey == "stable" ? option.GroupKey == "beta" : option.GroupKey == "stable"))
            ?? options.FirstOrDefault(option => option.Token != left.Token)
            ?? options[0];

        _updatingCompare = true;
        CompareLeftCombo.ItemsSource = options;
        CompareRightCombo.ItemsSource = options;
        CompareLeftCombo.SelectedItem = left;
        CompareRightCombo.SelectedItem = right;
        _updatingCompare = false;
        RenderComparison();
    }

    private void CompareSelection_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updatingCompare)
        {
            RenderComparison();
        }
    }

    private void RenderComparison()
    {
        var left = EntryFor(CompareLeftCombo.SelectedItem as VersionOption);
        var right = EntryFor(CompareRightCombo.SelectedItem as VersionOption);

        ApplyComparePanel(
            left,
            CompareVersionA,
            CompareChannelA,
            CompareTokenA,
            CompareLastUpdatedA,
            CompareAvailabilityA,
            CompareDescriptionA,
            CompareChangelogA,
            CompareInstalledBadgeA,
            CompareSelectedBadgeA,
            CompareSelectA);
        ApplyComparePanel(
            right,
            CompareVersionB,
            CompareChannelB,
            CompareTokenB,
            CompareLastUpdatedB,
            CompareAvailabilityB,
            CompareDescriptionB,
            CompareChangelogB,
            CompareInstalledBadgeB,
            CompareSelectedBadgeB,
            CompareSelectB);
        RenderDifference(left, right);
    }

    private void RenderDifference(ReleaseEntry? left, ReleaseEntry? right)
    {
        CompareDiffCard.IsVisible = left is not null && right is not null;
        if (left is null || right is null)
        {
            return;
        }
        var comparison = ChangelogComparer.Compare(
            left.Item.Changelog,
            right.Item.Changelog,
            Loc.Get("changelogUnavailable"));
        if (!comparison.IsAvailable)
        {
            CompareDiffText.Text = Loc.Get("compareNoChangelog");
        }
        else if (comparison.IsIdentical)
        {
            CompareDiffText.Text = Loc.Get("compareSameChangelog");
        }
        else
        {
            var sections = new List<string>();
            if (comparison.OnlyInRight.Count > 0)
            {
                sections.Add(Loc.Get("compareOnlyInB") + Environment.NewLine
                    + FormatDiffLines(comparison.OnlyInRight, "+ "));
            }
            if (comparison.OnlyInLeft.Count > 0)
            {
                sections.Add(Loc.Get("compareOnlyInA") + Environment.NewLine
                    + FormatDiffLines(comparison.OnlyInLeft, "- "));
            }
            CompareDiffText.Text = string.Join(Environment.NewLine + Environment.NewLine, sections);
        }

        var metadata = new List<string>();
        AddDifference(metadata, Loc.Get("channel"), left.GroupTitle, right.GroupTitle);
        AddDifference(metadata, Loc.Get("releaseDescription"),
            left.Item.Description ?? Loc.Get("notAvailable"),
            right.Item.Description ?? Loc.Get("notAvailable"));
        AddDifference(metadata, Loc.Get("manifest"),
            left.Item.ManifestDescription ?? Loc.Get("notAvailable"),
            right.Item.ManifestDescription ?? Loc.Get("notAvailable"));
        AddDifference(metadata, Loc.Get("backendToken"), left.Option.Token, right.Option.Token);
        AddDifference(metadata, Loc.Get("lastUpdated"),
            left.Item.LastUpdated ?? Loc.Get("notAvailable"),
            right.Item.LastUpdated ?? Loc.Get("notAvailable"));
        AddDifference(metadata, Loc.Get("availability"),
            AvailabilityText(left), AvailabilityText(right));
        CompareMetadataDiff.Text = metadata.Count == 0
            ? Loc.Get("compareSameMetadata")
            : string.Join(Environment.NewLine, metadata);
    }

    private static void AddDifference(List<string> lines, string label, string left, string right)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal))
        {
            lines.Add(label + ": " + left + " / " + right);
        }
    }

    private static string FormatDiffLines(IReadOnlyList<string> lines, string prefix)
    {
        const int limit = 8;
        var formatted = lines.Take(limit).Select(line => prefix + line).ToList();
        if (lines.Count > limit)
        {
            formatted.Add("…");
        }
        return string.Join(Environment.NewLine, formatted);
    }

    private ReleaseEntry? EntryFor(VersionOption? option)
        => option is null
            ? null
            : _entries.FirstOrDefault(entry => entry.Option.Token == option.Token);

    private static void ApplyComparePanel(
        ReleaseEntry? entry,
        TextBlock version,
        TextBlock channel,
        TextBlock token,
        TextBlock lastUpdated,
        TextBlock availability,
        TextBlock description,
        TextBlock changelog,
        Border installedBadge,
        Border selectedBadge,
        Button select)
    {
        if (entry is null)
        {
            version.Text = Loc.Get("notAvailable");
            channel.Text = string.Empty;
            token.Text = string.Empty;
            lastUpdated.Text = string.Empty;
            availability.Text = string.Empty;
            description.Text = string.Empty;
            changelog.Text = string.Empty;
            installedBadge.IsVisible = false;
            selectedBadge.IsVisible = false;
            select.Content = Loc.Get("useThisRelease");
            select.IsVisible = false;
            return;
        }

        version.Text = entry.Option.DisplayLabel + " · " + entry.Option.VersionText;
        channel.Text = entry.GroupTitle + " · " + entry.GroupHint;
        token.Text = entry.Option.Token;
        lastUpdated.Text = entry.Item.LastUpdated ?? Loc.Get("notAvailable");
        availability.Text = entry.Item.IsCached
            ? Loc.Get("catalogueCached") + " · " + Loc.Get("cachedNotGuaranteed")
            : Loc.Get("catalogueLive");
        description.Text = entry.Item.Description ?? Loc.Get("notAvailable");
        changelog.Text = entry.Item.Changelog ?? Loc.Get("changelogUnavailable");
        installedBadge.IsVisible = entry.Item.IsInstalled;
        selectedBadge.IsVisible = entry.Item.IsSelected;
        select.Content = Loc.Get("useThisRelease");
        select.IsVisible = true;
    }

    private void SelectCompareA_Click(object? sender, RoutedEventArgs e)
    {
        if (CompareLeftCombo.SelectedItem is VersionOption option)
        {
            UseRelease(EntryFor(option));
        }
    }

    private void SelectCompareB_Click(object? sender, RoutedEventArgs e)
    {
        if (CompareRightCombo.SelectedItem is VersionOption option)
        {
            UseRelease(EntryFor(option));
        }
    }

    private void SyncFilterClasses()
    {
        foreach (var button in ReleaseFilters.Children.OfType<Button>())
        {
            if (button.Tag is not string tag)
            {
                continue;
            }
            var selected = string.Equals(button.Tag as string, _filter, StringComparison.OrdinalIgnoreCase);
            if (selected && !button.Classes.Contains("selected"))
            {
                button.Classes.Add("selected");
            }
            else if (!selected)
            {
                button.Classes.Remove("selected");
            }
        }
    }

    private ReleaseFilter CurrentFilter => _filter switch
    {
        "stable" => ReleaseFilter.Stable,
        "beta" => ReleaseFilter.Beta,
        "pinned" => ReleaseFilter.Pinned,
        "installed" => ReleaseFilter.Installed,
        "selected" => ReleaseFilter.Selected,
        _ => ReleaseFilter.All,
    };

    private ReleaseSort CurrentSort => _sort switch
    {
        "version" => ReleaseSort.NewestFirst,
        "oldest" => ReleaseSort.OldestFirst,
        "stable" => ReleaseSort.StableFirst,
        "beta" => ReleaseSort.BetaFirst,
        _ => ReleaseSort.CatalogueOrder,
    };

    private static int SortIndex(string sort) => sort switch
    {
        "version" => 1,
        "oldest" => 2,
        "stable" => 3,
        "beta" => 4,
        _ => 0,
    };

    private IEnumerable<IGrouping<string, ReleaseEntry>> OrderGroups()
    {
        var groups = _visibleEntries.GroupBy(entry => entry.Item.ChannelKey).ToList();
        var version = Comparer<string?>.Create(ReleaseVersionComparer.Compare);
        return _sort switch
        {
            "version" => groups
                .OrderByDescending(group => group.Max(entry => entry.Item.Version), version)
                .ThenBy(group => group.Min(entry => entry.Item.Entry.Ordinal)),
            "oldest" => groups
                .OrderBy(group => group.Min(entry => entry.Item.Version), version)
                .ThenBy(group => group.Min(entry => entry.Item.Entry.Ordinal)),
            "stable" or "beta" => groups
                .OrderBy(group => GroupRank(group.Key))
                .ThenBy(group => group.Min(entry => entry.Item.Entry.Ordinal)),
            _ => groups
                .OrderBy(group => group.Min(entry => entry.Item.Entry.Ordinal)),
        };
    }

    private int GroupRank(string key)
    {
        var stableFirst = _sort == "stable";
        var betaFirst = _sort == "beta";
        if (stableFirst)
        {
            return key switch { "stable" => 0, "beta" => 1, "pinned" => 2, _ => 3 };
        }
        if (betaFirst)
        {
            return key switch { "beta" => 0, "stable" => 1, "pinned" => 2, _ => 3 };
        }
        return key switch { "stable" => 0, "beta" => 1, "pinned" => 2, _ => 3 };
    }

    private static bool MatchesToken(ReleaseEntry entry, string? token)
        => token is not null && entry.Option.Token == token;

    private static string AccessibleDescription(ReleaseEntry entry)
    {
        var badges = new List<string>();
        if (entry.Item.IsInstalled) badges.Add(Loc.Get("installed"));
        if (entry.Item.IsSelected) badges.Add(Loc.Get("selected"));
        if (entry.Option.IsUpToDate) badges.Add(Loc.Get("upToDateShort"));
        return entry.Option.DisplayLabel + ", " + entry.Option.VersionText
            + ", " + entry.GroupTitle
            + (badges.Count == 0 ? string.Empty : ", " + string.Join(", ", badges));
    }

    private static string Shorten(string text, int limit)
        => text.Length <= limit ? text : text[..limit].TrimEnd() + "…";
}
