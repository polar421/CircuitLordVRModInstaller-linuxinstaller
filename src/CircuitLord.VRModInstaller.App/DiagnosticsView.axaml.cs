using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Dedicated diagnostics destination. It reads the same InstallerViewModel and
/// installed-service state as Settings and the installer screen; it does not
/// run a second detector or change the active OpenXR runtime.
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private InstallerViewModel? _vm;
    private bool _viewModelSubscribed;
    private bool _logsSubscribed;
    private bool _languageSubscribed;
    private ScrollViewer? _logScrollViewer;
    private double _lastLogOffsetY;
    private bool _followLogs = true;
    private string _logFilter = string.Empty;
    private List<string> _visibleLogs = new();
    private double _savedPageOffset;
    private double _savedLogOffset;

    public DiagnosticsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToLanguage();
            if (_vm is not null)
            {
                SubscribeToViewModel();
                SubscribeToLogs();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnsubscribeFromLanguage();
            UnsubscribeFromLogs();
            UnsubscribeFromViewModel();
        };
    }

    public event EventHandler? OpenLogDirectoryRequested;

    public event Func<Task>? RepairSteamRequested;

    public void Open(InstallerViewModel vm)
    {
        _vm = vm;
        SubscribeToViewModel();
        SubscribeToLogs();
        IsVisible = true;
        Render();
        RefreshLogs(follow: false);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible)
                {
                    EnsureLogScrollViewer();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Reveals the existing view without resetting filters or scroll.</summary>
    public void Activate(InstallerViewModel vm)
    {
        _vm = vm;
        SubscribeToViewModel();
        SubscribeToLogs();
        IsVisible = true;
        Render();
        RefreshLogs(follow: true);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible)
                {
                    EnsureLogScrollViewer();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Captures the diagnostics page and log scroll positions.</summary>
    public void SaveScrollState()
    {
        _savedPageOffset = this.FindControl<ScrollViewer>("DiagnosticsScroll")?.Offset.Y
            ?? _savedPageOffset;
        if (_logScrollViewer is not null)
        {
            _savedLogOffset = _logScrollViewer.Offset.Y;
        }
    }

    /// <summary>Restores page/log scroll after the tab has been measured again.</summary>
    public void RestoreScrollState()
    {
        void Restore()
        {
            if (!IsVisible)
            {
                return;
            }
            var page = this.FindControl<ScrollViewer>("DiagnosticsScroll");
            if (page is not null)
            {
                RestoreScrollOffset(page, _savedPageOffset);
            }
            if (!_followLogs && _logScrollViewer is not null)
            {
                RestoreScrollOffset(_logScrollViewer, _savedLogOffset);
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

    public void RefreshAppearance()
    {
        if (IsVisible)
        {
            Render();
        }
    }

    public bool HandleEscape() => false;

    public void RefreshAfterOperation()
    {
        if (IsVisible)
        {
            Render();
            RefreshLogs(follow: false);
        }
    }

    private void SubscribeToViewModel()
    {
        if (_vm is null || _viewModelSubscribed)
        {
            return;
        }

        _vm.Changed += ViewModelChanged;
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
        if (IsVisible)
        {
            Render();
        }
    }

    private void SubscribeToLogs()
    {
        if (_vm is null || _logsSubscribed)
        {
            return;
        }

        _vm.LogLines.CollectionChanged += LogLines_CollectionChanged;
        _logsSubscribed = true;
    }

    private void UnsubscribeFromLogs()
    {
        if (_vm is null || !_logsSubscribed)
        {
            return;
        }

        _vm.LogLines.CollectionChanged -= LogLines_CollectionChanged;
        _logsSubscribed = false;
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
            if (IsVisible)
            {
                Render();
                RefreshLogs(follow: false);
            }
        });

    private void Render()
    {
        if (_vm is null)
        {
            return;
        }

        SetValue("DiagnosticsSteamStatus",
            _vm.SteamStatusText + " · " + _vm.SteamTypeText + " · " + _vm.SteamInstallationText);
        SetStatus("DiagnosticsTitanfallStatus", _vm.GameDetected,
            _vm.GameDetected ? Loc.Get("detected") : Loc.Get("notDetected"));
        SetValue("DiagnosticsGamePath",
            string.IsNullOrWhiteSpace(_vm.TitanfallGameDirectory) ? Loc.Get("notDetected") : _vm.TitanfallGameDirectory);
        SetValue("DiagnosticsWinePrefix", _vm.WinePrefixText);
        SetValue("DiagnosticsInstalledVersion", _vm.InstalledVersionText);

        SetStatus("DiagnosticsBackendOrigin", _vm.BackendOriginVerified,
            _vm.BackendOriginVerified ? Loc.Get("originVerified") : Loc.Get("originNotVerified"));
        SetValue("DiagnosticsBackendCommit", _vm.BackendCurrentCommitText);
        SetValue("DiagnosticsWorkingTree", _vm.BackendWorkingTreeText);
        SetStatusText("DiagnosticsInstallerScript", _vm.BackendInstallerScriptAvailable,
            !_vm.BackendOriginVerified
                ? Loc.Get("originNotVerified")
                : _vm.BackendInstallerScriptAvailable ? Loc.Get("detected") : Loc.Get("notDetected"));
        SetValue("DiagnosticsLastHelperUpdate", _vm.BackendLastUpdateText);

        SetValue("DiagnosticsOpenXr", _vm.OpenXrStatusText);
        SetValue("DiagnosticsRuntimeJson", _vm.OpenXrRuntimeJsonPath);
        SetValue("DiagnosticsSteamVr", _vm.SteamVrStatusText);
        SetValue("DiagnosticsWiVrn", _vm.WiVrnStatusText);
        SetValue("DiagnosticsMonado", _vm.MonadoStatusText);
        SetValue("DiagnosticsOpenVrBridge", _vm.OpenVrBridgeStatusText);
        SetValue("DiagnosticsLauncher", _vm.LauncherStatusText);

        SetStatusText("DiagnosticsDesktopEntry", _vm.DesktopEntryInstalled,
            !_vm.DesktopEntryChecked
                ? Loc.Get("notChecked")
                : _vm.DesktopEntryInstalled ? Loc.Get("detected") : Loc.Get("notDetected"));
        SetValue("DiagnosticsDesktopValidation", _vm.DesktopEntryStatusText);
        SetValue("DiagnosticsSteamShortcut", _vm.SteamShortcutStatusText);
        SetValue("DiagnosticsSteamType", _vm.SteamTypeText);
        SetValue("DiagnosticsSteamAccount", _vm.SteamAccountStatusText);

        this.FindControl<TextBlock>("DiagnosticsRefreshText")!.Text = _vm.DiagnosticsLastRefreshedText;
        this.FindControl<TextBlock>("DiagnosticsLastPreflight")!.Text = _vm.LastPreflightText;
        this.FindControl<TextBlock>("DiagnosticsPreflightText")!.Text = _vm.PreflightText;

        var advice = new List<string>();
        var incomplete = !_vm.BackendChecked || !_vm.RuntimeChecked
            || !_vm.DesktopEntryChecked || !_vm.SteamShortcutChecked;
        if (incomplete)
        {
            advice.Add(Loc.Get("diagRefreshAdvice"));
        }
        if (!_vm.GameDetected)
        {
            advice.Add(Loc.Get("diagGameAdvice"));
        }
        if (_vm.BackendChecked && !_vm.BackendVerified)
        {
            advice.Add(Loc.Get("diagBackendAdvice"));
        }
        if (_vm.RuntimeChecked && !_vm.OpenXrDetected)
        {
            advice.Add(Loc.Get("diagRuntimeAdvice"));
        }
        if (_vm.DesktopEntryChecked && !_vm.DesktopEntryInstalled)
        {
            advice.Add(Loc.Get("diagDesktopAdvice"));
        }
        if (_vm.SteamShortcutChecked && !_vm.SteamShortcutInstalled)
        {
            advice.Add(Loc.Get("diagSteamAdvice"));
        }
        this.FindControl<TextBlock>("DiagnosticsAdvice")!.Text = advice.Count == 0
            ? Loc.Get("diagAllClear")
            : string.Join(Environment.NewLine, advice);

        this.FindControl<Button>("RunPreflightButton")!.IsEnabled =
            _vm.BackendVerified && !_vm.Busy;
        this.FindControl<Button>("RefreshDiagnosticsButton")!.IsEnabled = !_vm.Busy;
        this.FindControl<Button>("RepairWiVrnButton")!.IsEnabled = !_vm.Busy;
        this.FindControl<Button>("RepairSteamButton")!.IsEnabled =
            _vm.HasSteamAccount && !_vm.Busy;
        this.FindControl<Button>("CopyDiagnosticsButton")!.IsEnabled = !_vm.Busy;
        this.FindControl<Button>("OpenLogsButton")!.IsEnabled = !_vm.Busy;
    }

    private void SetValue(string name, string text)
    {
        var block = this.FindControl<TextBlock>(name);
        if (block is not null)
        {
            block.Text = text;
        }
    }

    private void SetStatus(string name, bool ok, string text)
        => SetStatusText(name, ok, text);

    private void SetStatusText(string name, bool ok, string text)
    {
        var block = this.FindControl<TextBlock>(name);
        if (block is null)
        {
            return;
        }

        block.Text = text;
        block.Foreground = BrushFor(ok ? "Green" : "TextDim");
    }

    private static IBrush BrushFor(string key)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.White;
    }

    private async void RunPreflight_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        await _vm.RunPreflightAsync();
        Render();
        RefreshLogs(follow: false);
    }

    private async void RefreshDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        await Task.Run(_vm.RefreshLocalState);
        Render();
        RefreshLogs(follow: false);
    }

    private async void RepairWiVrn_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        await _vm.RepairDesktopAsync();
        Render();
    }

    private async void RepairSteam_Click(object? sender, RoutedEventArgs e)
    {
        if (RepairSteamRequested is null)
        {
            return;
        }

        await RepairSteamRequested();
        Render();
    }

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(_vm.DiagnosticsReport));
        await clipboard.SetDataAsync(data);
        var button = this.FindControl<Button>("CopyDiagnosticsButton")!;
        button.Content = Loc.Get("copied");
        await Task.Delay(1200);
        button.Content = Loc.Get("copyDiagnosticReport");
    }

    private void OpenLogs_Click(object? sender, RoutedEventArgs e)
        => OpenLogDirectoryRequested?.Invoke(this, EventArgs.Empty);

    private void ViewLogs_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<StackPanel>("DiagnosticsLogsSection")?.BringIntoView();
        this.FindControl<TextBox>("DiagnosticsLogSearch")?.Focus();
    }

    private void LatestLog_Click(object? sender, RoutedEventArgs e)
    {
        _followLogs = true;
        ScheduleLogScrollToLatest();
    }

    private void DiagnosticsLogList_Loaded(object? sender, RoutedEventArgs e)
    {
        EnsureLogScrollViewer();
    }

    private void LogSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _logFilter = this.FindControl<TextBox>("DiagnosticsLogSearch")?.Text ?? string.Empty;
        _followLogs = true;
        RefreshLogs(follow: true);
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshLogs(follow: true);
        }
    }

    private void RefreshLogs(bool follow)
    {
        if (_vm is null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("DiagnosticsLogList");
        var empty = this.FindControl<TextBlock>("DiagnosticsLogsEmpty");
        if (list is null)
        {
            return;
        }

        var filter = _logFilter.Trim();
        var filtered = filter.Length == 0
            ? _vm.LogLines.ToList()
            : _vm.LogLines.Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!_visibleLogs.SequenceEqual(filtered))
        {
            _visibleLogs = filtered;
            list.ItemsSource = _visibleLogs;
            if (follow && _followLogs)
            {
                ScheduleLogScrollToLatest();
            }
        }

        if (empty is not null)
        {
            empty.Text = _vm.LogLines.Count == 0 ? Loc.Get("noLogs") : Loc.Get("noLogMatches");
            empty.IsVisible = _visibleLogs.Count == 0;
        }

        UpdateLatestButton();
    }

    private void EnsureLogScrollViewer()
    {
        if (_logScrollViewer is not null)
        {
            return;
        }

        var viewer = this.FindControl<ListBox>("DiagnosticsLogList")?
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        _logScrollViewer = viewer;
        ScrollViewer.SetIsScrollChainingEnabled(viewer, false);
        _lastLogOffsetY = viewer.Offset.Y;
        viewer.ScrollChanged += LogScrollViewer_ScrollChanged;
        UpdateLatestButton();
        if (_followLogs)
        {
            ScheduleLogScrollToLatest();
        }
    }

    private void LogScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_logScrollViewer is null)
        {
            return;
        }

        var max = Math.Max(0, _logScrollViewer.Extent.Height - _logScrollViewer.Viewport.Height);
        var current = _logScrollViewer.Offset.Y;
        if (current < _lastLogOffsetY - 0.5 && max - current > 2)
        {
            _followLogs = false;
        }
        else if (max - current <= 2)
        {
            _followLogs = true;
        }

        _lastLogOffsetY = current;
        UpdateLatestButton();
    }

    private void ScheduleLogScrollToLatest()
    {
        if (!_followLogs)
        {
            UpdateLatestButton();
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_followLogs)
                {
                    return;
                }

                if (_logScrollViewer is not null)
                {
                    var max = Math.Max(0, _logScrollViewer.Extent.Height - _logScrollViewer.Viewport.Height);
                    _logScrollViewer.SetCurrentValue(
                        ScrollViewer.OffsetProperty,
                        new Vector(_logScrollViewer.Offset.X, max));
                    _lastLogOffsetY = _logScrollViewer.Offset.Y;
                }

                UpdateLatestButton();
            },
            DispatcherPriority.Background);
    }

    private void UpdateLatestButton()
    {
        var button = this.FindControl<Button>("LatestLogButton");
        if (button is null)
        {
            return;
        }

        var canScroll = _logScrollViewer is not null
            && _logScrollViewer.Extent.Height > _logScrollViewer.Viewport.Height + 1;
        button.IsVisible = canScroll && !_followLogs;
    }
}
