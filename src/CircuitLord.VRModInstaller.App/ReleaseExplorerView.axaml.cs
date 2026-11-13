using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Core.Manifest;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Full release browser over the validated backend catalogue. It can change
/// the current selection, but never invents a token or installs directly.
/// </summary>
public partial class ReleaseExplorerView : UserControl
{
    private sealed record ReleaseEntry(
        VersionOption Option,
        string GroupTitle,
        string GroupHint,
        string Description,
        string? Changelog);

    private InstallerViewModel? _vm;
    private List<ReleaseEntry> _entries = new();
    private string _filter = "all";
    private string _sort = "order";
    // The sort combo's initial SelectedIndex raises SelectionChanged while
    // XAML is still populating the rest of the page.
    private bool _loading = true;
    private bool _updatingCompare;
    private bool _languageSubscribed;

    public ReleaseExplorerView()
    {
        InitializeComponent();
        _loading = false;
        AttachedToVisualTree += (_, _) => SubscribeToLanguage();
        DetachedFromVisualTree += (_, _) => UnsubscribeFromLanguage();
    }

    public event EventHandler? Closed;

    public void Open(InstallerViewModel vm)
    {
        _vm = vm;
        _filter = "all";
        _sort = "order";
        _loading = true;
        ReleaseSearchBox.Text = string.Empty;
        ReleaseSortCombo.SelectedIndex = 0;
        _loading = false;

        vm.RefreshLocalState();
        ReloadEntries();
        IsVisible = true;
        ShowList();
        RenderList();
        ReleaseListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible)
                {
                    Focus();
                    ReleaseSearchBox.Focus();
                }
            },
            DispatcherPriority.Loaded);
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
        CloseExplorer();
        return true;
    }

    public void RefreshAppearance()
    {
        if (!IsVisible)
        {
            return;
        }
        if (ReleaseCompareScroll.IsVisible)
        {
            RenderComparison();
        }
        else
        {
            RenderList();
        }
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

    private void LanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || _vm is null)
            {
                return;
            }
            ReloadEntries();
            if (ReleaseCompareScroll.IsVisible)
            {
                BuildCompareOptions();
            }
            else
            {
                RenderList();
            }
        });

    private void CloseExplorer_Click(object? sender, RoutedEventArgs e) => CloseExplorer();

    private void CloseExplorer()
    {
        HelpTip.CloseOpen();
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
        if (!_loading)
        {
            RenderList();
        }
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

    private void BackToReleases_Click(object? sender, RoutedEventArgs e)
    {
        ShowList();
        RenderList();
    }

    private void ShowList()
    {
        ReleaseListHeader.IsVisible = true;
        ReleaseCompareHeader.IsVisible = false;
        ReleaseToolbar.IsVisible = true;
        ReleaseListScroll.IsVisible = true;
        ReleaseCompareScroll.IsVisible = false;
    }

    private void ReloadEntries()
    {
        _entries = new List<ReleaseEntry>();
        if (_vm is null)
        {
            return;
        }

        foreach (var group in _vm.VersionGroups)
        {
            foreach (var option in group.Options)
            {
                var description = !string.IsNullOrWhiteSpace(option.ReleaseDescription)
                    ? option.ReleaseDescription
                    : !string.IsNullOrWhiteSpace(group.Hint) ? group.Hint : Loc.Get("notAvailable");
                var changelog = _vm.GetReleaseChangelog(option.Version);
                _entries.Add(new ReleaseEntry(
                    option,
                    group.Title,
                    group.Hint,
                    description,
                    changelog));
            }
        }
    }

    private void RenderList()
    {
        SyncFilterClasses();
        ReleaseListPanel.Children.Clear();

        var query = NormalizeSearch(ReleaseSearchBox.Text);
        var filtered = _entries
            .Where(entry => string.Equals(_filter, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Option.GroupKey, _filter, StringComparison.OrdinalIgnoreCase))
            .Where(entry => query.Length == 0 || MatchesSearch(entry, query))
            .ToList();

        if (string.Equals(_sort, "version", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered
                .OrderByDescending(entry => ParseVersion(entry.Option.Version))
                .ThenByDescending(entry => entry.Option.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Option.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var entry in filtered)
        {
            ReleaseListPanel.Children.Add(BuildCard(entry));
        }

        var empty = filtered.Count == 0;
        ReleaseEmptyState.IsVisible = empty;
        if (empty)
        {
            // Both no catalogue entries and no filter/search matches have the
            // same useful next action: refresh or broaden the query.
            ReleaseEmptyText.Text = Loc.Get("noReleases");
        }

        OpenCompareButton.IsEnabled = _entries.Count >= 2;
        ReleaseListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
    }

    private void SyncFilterClasses()
    {
        foreach (var button in ReleaseFilters.Children.OfType<Button>())
        {
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

    private static string NormalizeSearch(string? text)
    {
        var query = (text ?? string.Empty).Trim();
        return query.StartsWith('v') || query.StartsWith('V') ? query[1..] : query;
    }

    private static bool MatchesSearch(ReleaseEntry entry, string query)
    {
        var version = entry.Option.Version;
        return (version?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || entry.Option.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Option.Token.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static Version ParseVersion(string? version)
        => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0);

    private Border BuildCard(ReleaseEntry entry)
    {
        var card = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14, 12),
            Margin = default,
        };
        var stack = new StackPanel { Spacing = 7 };

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

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (entry.Option.IsInstalled)
        {
            badges.Children.Add(CreateBadge(Loc.Get("installed"), accent: false));
        }
        if (entry.Option.IsSelected)
        {
            badges.Children.Add(CreateBadge(Loc.Get("selected"), accent: true));
        }
        Grid.SetColumn(badges, 1);
        top.Children.Add(badges);
        stack.Children.Add(top);

        stack.Children.Add(new TextBlock
        {
            Text = entry.Description,
            Classes = { "small" },
            TextWrapping = TextWrapping.Wrap,
        });

        stack.Children.Add(new TextBlock
        {
            Text = Loc.Get("releaseNotes"),
            Classes = { "release-label" },
        });
        stack.Children.Add(new TextBlock
        {
            Text = entry.Changelog ?? Loc.Get("changelogUnavailable"),
            Classes = { "small" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        footer.Children.Add(new TextBlock
        {
            Text = entry.GroupTitle + " · " + entry.Option.KindLabel,
            Classes = { "small" },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var select = new Button
        {
            Content = Loc.Get("selectRelease"),
            Classes = { "ghost" },
            FontSize = 11.5,
            Padding = new Thickness(8, 4),
            IsVisible = !entry.Option.IsSelected,
        };
        select.Click += (_, _) => SelectRelease(entry.Option);
        Grid.SetColumn(select, 1);
        footer.Children.Add(select);
        stack.Children.Add(footer);

        card.Child = stack;
        return card;
    }

    private static Border CreateBadge(string text, bool accent)
    {
        var badge = new Border();
        badge.Classes.Add(accent ? "accent-pill" : "status-pill");
        var label = new TextBlock { Text = text };
        label.Classes.Add(accent ? "pill-text" : "status-pill-text");
        badge.Child = label;
        return badge;
    }

    private void SelectRelease(VersionOption option)
    {
        if (_vm is null || !_vm.SelectReleaseToken(option.Token))
        {
            return;
        }

        ReloadEntries();
        if (ReleaseCompareScroll.IsVisible)
        {
            BuildCompareOptions();
        }
        else
        {
            RenderList();
        }
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
        ReleaseListScroll.IsVisible = false;
        ReleaseCompareScroll.IsVisible = true;
        BuildCompareOptions();
        ReleaseCompareScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
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

        var left = options.FirstOrDefault(option => string.Equals(option.Token, previousLeft, StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => option.IsSelected)
            ?? options[0];
        var right = options.FirstOrDefault(option =>
                string.Equals(option.Token, previousRight, StringComparison.Ordinal)
                && !string.Equals(option.Token, left.Token, StringComparison.Ordinal))
            ?? options.FirstOrDefault(option =>
                !string.Equals(option.Token, left.Token, StringComparison.Ordinal)
                && option.GroupKey == "beta")
            ?? options.FirstOrDefault(option => !string.Equals(option.Token, left.Token, StringComparison.Ordinal))
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
            CompareInstalledBadgeA,
            CompareSelectedBadgeA,
            CompareDescriptionA,
            CompareChangelogA,
            CompareSelectA);
        ApplyComparePanel(
            right,
            CompareVersionB,
            CompareChannelB,
            CompareInstalledBadgeB,
            CompareSelectedBadgeB,
            CompareDescriptionB,
            CompareChangelogB,
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
            left.Changelog,
            right.Changelog,
            Loc.Get("changelogUnavailable"));
        if (!comparison.IsAvailable)
        {
            CompareDiffText.Text = Loc.Get("compareNoChangelog");
            return;
        }
        if (comparison.IsIdentical)
        {
            CompareDiffText.Text = Loc.Get("compareSameChangelog");
            return;
        }

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

    private static string FormatDiffLines(IReadOnlyList<string> lines, string prefix)
    {
        const int limit = 8;
        var formatted = lines
            .Take(limit)
            .Select(line => prefix + line)
            .ToList();
        if (lines.Count > limit)
        {
            formatted.Add("…");
        }
        return string.Join(Environment.NewLine, formatted);
    }

    private ReleaseEntry? EntryFor(VersionOption? option)
        => option is null
            ? null
            : _entries.FirstOrDefault(entry =>
                string.Equals(entry.Option.Token, option.Token, StringComparison.Ordinal));

    private static void ApplyComparePanel(
        ReleaseEntry? entry,
        TextBlock version,
        TextBlock channel,
        Border installedBadge,
        Border selectedBadge,
        TextBlock description,
        TextBlock changelog,
        Button select)
    {
        if (entry is null)
        {
            version.Text = Loc.Get("notAvailable");
            channel.Text = string.Empty;
            description.Text = string.Empty;
            changelog.Text = string.Empty;
            installedBadge.IsVisible = false;
            selectedBadge.IsVisible = false;
            select.Content = Loc.Get("selectRelease");
            select.IsVisible = false;
            return;
        }

        version.Text = entry.Option.DisplayLabel;
        channel.Text = entry.GroupTitle + " · " + entry.Option.VersionText;
        description.Text = entry.Description;
        changelog.Text = entry.Changelog ?? Loc.Get("changelogUnavailable");
        installedBadge.IsVisible = entry.Option.IsInstalled;
        selectedBadge.IsVisible = entry.Option.IsSelected;
        select.Content = Loc.Get("selectRelease");
        select.IsVisible = !entry.Option.IsSelected;
    }

    private void SelectCompareA_Click(object? sender, RoutedEventArgs e)
    {
        if (CompareLeftCombo.SelectedItem is VersionOption option)
        {
            SelectRelease(option);
        }
    }

    private void SelectCompareB_Click(object? sender, RoutedEventArgs e)
    {
        if (CompareRightCombo.SelectedItem is VersionOption option)
        {
            SelectRelease(option);
        }
    }
}
