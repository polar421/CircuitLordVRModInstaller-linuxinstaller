using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Dedicated installation history page. The compact installer summary links
/// here, and each operation opens a report containing its persisted log.
/// </summary>
public partial class HistoryView : UserControl
{
    private InstallerViewModel? _vm;
    private IReadOnlyList<InstallerOperationRecord> _records = Array.Empty<InstallerOperationRecord>();
    private InstallerOperationRecord? _selected;
    private string _filter = "all";
    private string _statusFilter = "all";
    private string _searchText = string.Empty;
    private string _sort = "newest";
    private bool _languageSubscribed;
    private bool _viewModelSubscribed;
    private bool _embedded;
    private string _historySignature = string.Empty;
    private double _savedListOffset;
    private double _savedDetailOffset;

    public HistoryView()
    {
        InitializeComponent();
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
        _vm = vm;
        _embedded = embedded;
        HistoryBackButton.IsVisible = !embedded;
        _filter = "all";
        _statusFilter = "all";
        _searchText = string.Empty;
        _sort = "newest";
        HistorySearchBox.Text = string.Empty;
        HistorySortCombo.SelectedIndex = 0;
        _selected = null;
        SubscribeToViewModel(vm);
        if (!vm.Busy)
        {
            vm.RefreshLocalState();
        }
        _records = vm.OperationHistory;
        IsVisible = true;
        ShowList();
        Render();
        _savedListOffset = 0;
        _savedDetailOffset = 0;
        HistoryListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible)
                {
                    Focus();
                    if (_embedded)
                    {
                        HistorySearchBox.Focus();
                    }
                    else
                    {
                        HistoryBackButton.Focus();
                    }
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Restores an already-open History tab without clearing filters.</summary>
    public void Activate(InstallerViewModel vm)
    {
        _vm = vm;
        SubscribeToViewModel(vm);
        IsVisible = true;
        SyncFilterClasses();
        if (HistorySignature() != _historySignature)
        {
            Render();
        }
    }

    /// <summary>Captures the active list/detail scroll offset before hiding.</summary>
    public void SaveScrollState()
    {
        if (HistoryListScroll.IsVisible)
        {
            _savedListOffset = HistoryListScroll.Offset.Y;
        }
        if (HistoryDetailScroll.IsVisible)
        {
            _savedDetailOffset = HistoryDetailScroll.Offset.Y;
        }
    }

    /// <summary>Restores the active list/detail scroll offset after layout.</summary>
    public void RestoreScrollState()
    {
        void Restore()
        {
            if (!IsVisible)
            {
                return;
            }
            if (HistoryListScroll.IsVisible)
            {
                RestoreScrollOffset(HistoryListScroll, _savedListOffset);
            }
            if (HistoryDetailScroll.IsVisible)
            {
                RestoreScrollOffset(HistoryDetailScroll, _savedDetailOffset);
            }
        }

        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
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
        if (_selected is not null)
        {
            BackToHistory();
            return true;
        }
        if (_embedded)
        {
            OverviewRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        CloseHistory();
        return true;
    }

    public void RefreshAppearance()
    {
        if (IsVisible)
        {
            SaveScrollState();
            Render();
            RestoreScrollState();
        }
    }

    private void SubscribeToViewModel(InstallerViewModel vm)
    {
        if (_viewModelSubscribed)
        {
            if (ReferenceEquals(_vm, vm))
            {
                return;
            }
            UnsubscribeFromViewModel();
        }

        _vm = vm;
        vm.Changed += ViewModelChanged;
        _viewModelSubscribed = true;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_vm is null || !_viewModelSubscribed)
        {
            return;
        }
        _vm.Changed -= ViewModelChanged;
        _viewModelSubscribed = false;
    }

    private void ViewModelChanged(object? sender, EventArgs e)
    {
        if (!IsVisible || _vm is null || HistorySignature() == _historySignature)
        {
            return;
        }

        SaveScrollState();
        Render();
        RestoreScrollState();
    }

    private string HistorySignature()
        => _vm is null
            ? string.Empty
            : string.Join("|", _vm.OperationHistory.Select(record =>
                record.OperationId + "=" + record.Result));

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
            if (IsVisible)
            {
                SaveScrollState();
                Render();
                RestoreScrollState();
            }
        });

    private void CloseHistory_Click(object? sender, RoutedEventArgs e) => CloseHistory();

    private void CloseHistory()
    {
        HelpTip.CloseOpen();
        if (_embedded)
        {
            OverviewRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        UnsubscribeFromViewModel();
        IsVisible = false;
        _selected = null;
        _vm = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void BackToHistory_Click(object? sender, RoutedEventArgs e) => BackToHistory();

    private void BackToHistory()
    {
        _selected = null;
        ShowList();
        Render();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsVisible)
                {
                    return;
                }
                var first = HistoryListPanel.Children.OfType<Button>().FirstOrDefault();
                (first ?? HistoryBackButton).Focus();
            },
            DispatcherPriority.Loaded);
    }

    private void History_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HandleEscape())
        {
            e.Handled = true;
        }
    }

    private void Filter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter })
        {
            return;
        }
        _filter = filter;
        _selected = null;
        ShowList();
        Render();
        HistoryListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
    }

    private void StatusFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter })
        {
            return;
        }
        _statusFilter = filter;
        _selected = null;
        ShowList();
        Render();
        HistoryListScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
    }

    private void HistorySearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchText = HistorySearchBox.Text ?? string.Empty;
        if (_selected is not null)
        {
            _selected = null;
            ShowList();
        }
        RenderList();
    }

    private void HistorySort_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is set while the XAML is still being populated; the
        // fields used by RenderList do not exist yet at that point.
        if (_vm is null)
        {
            return;
        }
        _sort = (HistorySortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "newest";
        RenderList();
    }

    private void ShowList()
    {
        HistoryListHeader.IsVisible = true;
        HistoryBackButton.IsVisible = !_embedded;
        HistoryDetailHeader.IsVisible = false;
        HistoryFilters.IsVisible = true;
        HistoryStatusFilters.IsVisible = true;
        HistoryListScroll.IsVisible = true;
        HistoryDetailScroll.IsVisible = false;
    }

    private void ShowDetail(InstallerOperationRecord record)
    {
        _selected = record;
        HistoryListHeader.IsVisible = false;
        HistoryDetailHeader.IsVisible = true;
        HistoryFilters.IsVisible = false;
        HistoryStatusFilters.IsVisible = false;
        HistoryListScroll.IsVisible = false;
        HistoryDetailScroll.IsVisible = true;
        RenderDetail(record);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible && _selected is not null)
                {
                    HistoryBackToHistoryButton.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    private void Render()
    {
        if (_vm is null)
        {
            return;
        }

        _records = _vm.OperationHistory;
        _historySignature = HistorySignature();
        if (_selected is not null)
        {
            var selectedId = _selected.OperationId;
            _selected = _records.FirstOrDefault(
                record => string.Equals(record.OperationId, selectedId, StringComparison.Ordinal));
            if (_selected is not null)
            {
                RenderDetail(_selected);
            }
            else
            {
                ShowList();
            }
        }

        SyncFilterClasses();
        if (_selected is null)
        {
            RenderList();
        }
    }

    private void SyncFilterClasses()
    {
        foreach (var button in HistoryFilters.Children.OfType<Button>())
        {
            SetFilterClass(button, _filter);
        }
        foreach (var button in HistoryStatusFilters.Children.OfType<Button>())
        {
            SetFilterClass(button, _statusFilter);
        }
    }

    private static void SetFilterClass(Button button, string active)
    {
        var selected = string.Equals(button.Tag as string, active, StringComparison.OrdinalIgnoreCase);
        if (selected && !button.Classes.Contains("selected"))
        {
            button.Classes.Add("selected");
        }
        else if (!selected)
        {
            button.Classes.Remove("selected");
        }
    }

    private void RenderList()
    {
        HistoryListPanel.Children.Clear();
        var query = _searchText.Trim();
        var filtered = _records
            .Where(record => string.Equals(_filter, "all", StringComparison.OrdinalIgnoreCase)
                || EffectiveKind(record).ToString().Equals(_filter, StringComparison.OrdinalIgnoreCase))
            .Where(record => MatchesStatus(record, _statusFilter))
            .Where(record => query.Length == 0 || MatchesSearch(record, query))
            .OrderBy(record => record.StartedAt)
            .ToList();
        if (!string.Equals(_sort, "oldest", StringComparison.OrdinalIgnoreCase))
        {
            filtered.Reverse();
        }

        foreach (var record in filtered)
        {
            HistoryListPanel.Children.Add(BuildRow(record));
        }

        var empty = filtered.Count == 0;
        HistoryEmptyState.IsVisible = empty;
        if (empty)
        {
            HistoryEmptyText.Text = _vm?.OperationHistoryUnavailable == true
                ? Loc.Get("historyUnavailable")
                : _records.Count == 0 ? Loc.Get("historyEmpty") : Loc.Get("historyEmptyFilter");
        }
    }

    private Button BuildRow(InstallerOperationRecord record)
    {
        var button = new Button
        {
            Tag = record,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        button.Classes.Add("historyrow");
        button.Click += (_, _) => ShowDetail(record);
        ToolTip.SetTip(button, Loc.Get("historyReport"));
        AutomationProperties.SetName(
            button,
            OperationLabel(record) + ", " + FormatDate(record) + ", "
            + VersionTransition(record) + ", " + ResultLabel(record));

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 4,
        };

        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        heading.Children.Add(new TextBlock
        {
            Text = OperationLabel(record),
            Classes = { "history-type" },
        });
        heading.Children.Add(BuildStatusPill(record));
        top.Children.Add(heading);

        var date = new TextBlock
        {
            Text = FormatDate(record),
            Classes = { "small" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(date, 1);
        top.Children.Add(date);
        content.Children.Add(top);

        var versions = new TextBlock
        {
            Text = VersionTransition(record),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetRow(versions, 1);
        content.Children.Add(versions);

        var meta = new TextBlock
        {
            Text = BuildMeta(record),
            Classes = { "small" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(meta, 2);
        content.Children.Add(meta);

        button.Content = content;
        return button;
    }

    private Border BuildStatusPill(InstallerOperationRecord record)
    {
        var statusClass = StatusClass(record);
        var pill = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        pill.Classes.Add("status-pill");
        pill.Classes.Add(statusClass);
        var text = new TextBlock
        {
            Text = ResultLabel(record),
        };
        text.Classes.Add("status-pill-text");
        text.Classes.Add(statusClass);
        pill.Child = text;
        return pill;
    }

    private void RenderDetail(InstallerOperationRecord record)
    {
        HistoryDetailType.Text = OperationLabel(record);
        HistoryDetailDate.Text = FormatDate(record);
        HistoryDetailMeta.Text = OperationLabel(record) + " · " + FormatDate(record);
        SetStatusPill(record);

        HistoryDetailDateValue.Text = FormatDate(record);
        HistoryDetailPreviousVersion.Text = PreviousVersionText(record);
        HistoryDetailNewVersion.Text = NewVersionText(record);
        HistoryDetailChannel.Text = ChannelLabel(record);
        HistoryDetailOperationType.Text = OperationLabel(record);
        HistoryDetailResult.Text = ResultLabel(record);
        HistoryDetailBackendCommit.Text = string.IsNullOrWhiteSpace(record.BackendCommit)
            ? Loc.Get("notAvailable")
            : record.BackendCommit;
        HistoryDetailDuration.Text = DurationLabel(record);
        HistoryDetailExitCode.Text = record.ExitCode?.ToString(CultureInfo.CurrentCulture)
            ?? Loc.Get("notAvailable");
        HistoryDetailLogPath.Text = string.IsNullOrWhiteSpace(record.LogPath)
            ? Loc.Get("historyLogMissing")
            : record.LogPath;
        HistoryDetailLogFile.Text = string.IsNullOrWhiteSpace(record.LogPath)
            ? string.Empty
            : Path.GetFileName(record.LogPath);
        HistoryDetailLogText.Text = _vm?.ReadOperationLog(record) ?? Loc.Get("historyLogMissing");
    }

    private void SetStatusPill(InstallerOperationRecord record)
    {
        var statusClass = StatusClass(record);
        HistoryDetailStatusPill.Classes.Clear();
        HistoryDetailStatusPill.Classes.Add("status-pill");
        HistoryDetailStatusPill.Classes.Add(statusClass);
        HistoryDetailStatusText.Classes.Clear();
        HistoryDetailStatusText.Classes.Add("status-pill-text");
        HistoryDetailStatusText.Classes.Add(statusClass);
        HistoryDetailStatusText.Text = ResultLabel(record);
    }

    private static bool MatchesStatus(InstallerOperationRecord record, string filter)
    {
        if (string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var result = ResultKey(record);
        return string.Equals(filter, result, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResultKey(InstallerOperationRecord record) => record.Result switch
    {
        "Succeeded" => "succeeded",
        "Failed" => "failed",
        "Running" => "running",
        "Cancelled" or "Canceled" => "cancelled",
        _ => "unknown",
    };

    private static bool MatchesSearch(InstallerOperationRecord record, string query)
    {
        var haystack = string.Join(' ', new[]
        {
            OperationLabel(record),
            record.RequestedDisplayName,
            record.RequestedToken,
            PreviousVersionText(record),
            NewVersionText(record),
            ChannelLabel(record),
            record.BackendCommit,
            resultLabelForSearch(record),
            record.LogPath,
        });
        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string resultLabelForSearch(InstallerOperationRecord record) => record.Result ?? string.Empty;

    private static string StatusClass(InstallerOperationRecord record) => record.Result switch
    {
        "Succeeded" => "success",
        "Running" => "running",
        "Failed" => "failed",
        "Cancelled" or "Canceled" => "cancelled",
        _ => "unknown",
    };

    private static string OperationLabel(InstallerOperationRecord record) => EffectiveKind(record) switch
    {
        BackendOperationKind.Install => Loc.Get("install"),
        BackendOperationKind.Update => Loc.Get("update"),
        BackendOperationKind.Reinstall => Loc.Get("reinstall"),
        BackendOperationKind.Repair => Loc.Get("repair"),
        BackendOperationKind.Uninstall => Loc.Get("uninstall"),
        _ => record.Kind.ToString(),
    };

    private static string ResultLabel(InstallerOperationRecord record) => record.Result switch
    {
        "Succeeded" => Loc.Get("operationSucceeded"),
        "Running" => Loc.Get("operationRunning"),
        "Failed" => Loc.Get("operationFailed"),
        "Cancelled" or "Canceled" => Loc.Get("operationCancelled"),
        _ => Loc.Get("operationUnknown"),
    };

    private static string PreviousVersionText(InstallerOperationRecord record)
        => VersionLabel(record.OldVersion)
           ?? (EffectiveKind(record) == BackendOperationKind.Install
                ? Loc.Get("notInstalled")
                : Loc.Get("notAvailable"));

    private static string NewVersionText(InstallerOperationRecord record)
        => EffectiveKind(record) == BackendOperationKind.Uninstall
            ? Loc.Get("notInstalled")
            : VersionLabel(record.NewVersion) ?? Loc.Get("notAvailable");

    private static string VersionTransition(InstallerOperationRecord record)
        => PreviousVersionText(record) + "  →  " + NewVersionText(record);

    /// <summary>
    /// Older builds recorded every force install as Reinstall, including
    /// channel updates. Infer an update when the versions differ so the new
    /// Update filter and report stay truthful for existing histories.
    /// </summary>
    private static BackendOperationKind EffectiveKind(InstallerOperationRecord record)
    {
        if (record.Kind != BackendOperationKind.Reinstall)
        {
            return record.Kind;
        }
        var previous = VersionLabel(record.OldVersion);
        var next = VersionLabel(record.NewVersion);
        return !string.IsNullOrWhiteSpace(previous)
            && !string.IsNullOrWhiteSpace(next)
            && !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase)
                ? BackendOperationKind.Update
                : record.Kind;
    }

    private static string? VersionLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        foreach (var field in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("mod=", StringComparison.Ordinal))
            {
                return field["mod=".Length..];
            }
        }

        // Older records stored the localized "Installed · version" string.
        var separator = trimmed.LastIndexOf('·');
        if (separator >= 0)
        {
            var suffix = trimmed[(separator + 1)..].Trim();
            if (suffix.Length > 0)
            {
                return suffix;
            }
        }

        return trimmed;
    }

    private static string ChannelLabel(InstallerOperationRecord record)
    {
        var channel = record.Channel;
        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = record.RequestedToken switch
            {
                "latest" => "stable",
                "beta" => "beta",
                null or "" => null,
                _ => "pinned",
            };
        }

        return channel switch
        {
            "stable" => Loc.Get("channelStable"),
            "beta" => Loc.Get("channelBeta"),
            "pinned" => Loc.Get("channelPinned"),
            _ => Loc.Get("channelUnknown"),
        };
    }

    private static string DurationLabel(InstallerOperationRecord record)
    {
        if (record.Duration is { } duration)
        {
            return FormatDuration(duration);
        }
        if (record.FinishedAt is { } finishedAt)
        {
            return FormatDuration(finishedAt - record.StartedAt);
        }
        return string.Equals(record.Result, "Running", StringComparison.Ordinal)
            ? Loc.Get("operationRunning")
            : Loc.Get("notAvailable");
    }

    private async void HistoryCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(OperationLabel(_selected));
        builder.AppendLine(Loc.Get("historyDate") + ": " + FormatDate(_selected));
        builder.AppendLine(Loc.Get("historyPreviousVersion") + ": " + PreviousVersionText(_selected));
        builder.AppendLine(Loc.Get("historyNewVersion") + ": " + NewVersionText(_selected));
        builder.AppendLine(Loc.Get("selectedChannel") + ": " + ChannelLabel(_selected));
        builder.AppendLine(Loc.Get("backendCommit") + ": " + (_selected.BackendCommit ?? Loc.Get("notAvailable")));
        builder.AppendLine(Loc.Get("historyResult") + ": " + ResultLabel(_selected));
        builder.AppendLine(Loc.Get("duration") + ": " + DurationLabel(_selected));
        builder.AppendLine(Loc.Get("exitCode") + ": " + (_selected.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? Loc.Get("notAvailable")));
        builder.AppendLine(Loc.Get("log") + ": " + (_selected.LogPath ?? Loc.Get("historyLogMissing")));
        builder.AppendLine();
        builder.AppendLine(_vm?.ReadOperationLog(_selected) ?? Loc.Get("historyLogMissing"));

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(builder.ToString()));
        await clipboard.SetDataAsync(data);

        var button = this.FindControl<Button>("HistoryCopyButton");
        if (button is null)
        {
            return;
        }
        button.Content = Loc.Get("copied");
        await Task.Delay(1200);
        button.Content = Loc.Get("historyCopyDiagnostics");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDate(InstallerOperationRecord record)
        => (record.FinishedAt ?? record.StartedAt).ToLocalTime().ToString("g");

    private static string BuildMeta(InstallerOperationRecord record)
    {
        var log = string.IsNullOrWhiteSpace(record.LogPath)
            ? Loc.Get("historyLogMissing")
            : Path.GetFileName(record.LogPath);
        var parts = new[]
        {
            Loc.Get("channel") + " " + ChannelLabel(record),
            Loc.Get("backendCommit") + " " + ShortCommit(record.BackendCommit),
            Loc.Get("duration") + " " + DurationLabel(record),
            Loc.Get("log") + " " + log,
        };
        return string.Join(" · ", parts);
    }

    private static string ShortCommit(string? commit)
        => commit is { Length: >= 7 } ? commit[..7] : commit ?? Loc.Get("notAvailable");
}
