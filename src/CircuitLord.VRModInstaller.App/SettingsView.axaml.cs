using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// The settings panel is deliberately self-contained: it owns only presentation,
/// while every action is delegated to <see cref="InstallerViewModel"/>.
/// </summary>
public partial class SettingsView : UserControl
{
    private InstallerViewModel? _vm;
    private TaskCompletionSource<bool>? _confirmation;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void Open(InstallerViewModel vm)
    {
        if (!ReferenceEquals(_vm, vm))
        {
            if (_vm is not null)
            {
                _vm.Changed -= ViewModel_Changed;
            }
            _vm = vm;
            DataContext = vm;
            vm.Changed += ViewModel_Changed;
        }

        _loading = true;
        this.FindControl<Border>("SettingsOverlay")!.IsVisible = true;
        IsVisible = true;
        this.FindControl<Border>("RuntimeOverlay")!.IsVisible = false;
        this.FindControl<Border>("LogsOverlay")!.IsVisible = false;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = false;
        this.FindControl<ToggleSwitch>("RoundedCornersToggle")!.IsChecked = vm.RoundedCorners;
        this.FindControl<ToggleSwitch>("AppUpdatesToggle")!.IsChecked = vm.AutoCheckForAppUpdates;
        this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked = vm.UpdateBackendAutomatically;
        this.FindControl<ToggleSwitch>("ConfirmSteamRestartToggle")!.IsChecked = vm.ConfirmSteamRestart;
        this.FindControl<ComboBox>("LanguageCombo")!.SelectedIndex = vm.Language == "de" ? 1 : 0;
        this.FindControl<ScrollViewer>("SettingsScroll")!.Offset = new Vector(0, 0);
        _loading = false;
        RefreshAll();
        this.FindControl<Button>("SettingsCloseButton")!.Focus();
    }

    private void CloseSettings()
    {
        this.FindControl<Border>("SettingsOverlay")!.IsVisible = false;
        IsVisible = false;
        if (_vm is not null)
        {
            _vm.Changed -= ViewModel_Changed;
            _vm = null;
        }
    }

    private void ViewModel_Changed(object? sender, EventArgs e)
    {
        if (IsVisible && !_loading)
        {
            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        if (_vm is null)
        {
            return;
        }

        this.FindControl<TextBlock>("SettingsCurrentAppVersion")!.Text = _vm.CurrentAppVersion;
        this.FindControl<TextBlock>("SettingsLatestAppVersion")!.Text = _vm.LatestAppVersionText;
        this.FindControl<TextBlock>("SettingsUpdateStatus")!.Text = _vm.UpdateStatusText;
        this.FindControl<Button>("SettingsOpenReleaseButton")!.IsEnabled = _vm.LatestReleaseUrl is not null;

        this.FindControl<TextBlock>("SettingsBackendCurrent")!.Text = _vm.BackendCurrentCommitText;
        this.FindControl<TextBlock>("SettingsBackendLatest")!.Text = _vm.BackendLatestCommitText;
        this.FindControl<TextBlock>("SettingsBackendWorkingTree")!.Text = _vm.BackendWorkingTreeText;
        this.FindControl<TextBlock>("SettingsBackendLastUpdate")!.Text = _vm.BackendLastUpdateText;
        this.FindControl<TextBlock>("SettingsBackendStatus")!.Text = _vm.BackendUpdateStatusText;
        this.FindControl<TextBlock>("SettingsBackendRecovery")!.Text = _vm.BackendRecoveryText;
        this.FindControl<ProgressBar>("SettingsBackendProgress")!.IsVisible = _vm.BackendProgressVisible;
        this.FindControl<Button>("SettingsBackendCheckButton")!.IsEnabled = !_vm.Busy && !_vm.BackendCheckInProgress;
        this.FindControl<Button>("SettingsBackendUpdateButton")!.IsEnabled = !_vm.Busy && !_vm.BackendCheckInProgress;

        this.FindControl<TextBlock>("SettingsOpenXr")!.Text = _vm.OpenXrStatusText;
        this.FindControl<TextBlock>("SettingsSteamVr")!.Text = _vm.SteamVrStatusText;
        this.FindControl<TextBlock>("SettingsWiVrn")!.Text = _vm.WiVrnStatusText;
        this.FindControl<TextBlock>("SettingsMonado")!.Text = _vm.MonadoStatusText;
        this.FindControl<TextBlock>("SettingsOpenVrBridge")!.Text = _vm.OpenVrBridgeStatusText;
        this.FindControl<TextBlock>("SettingsLauncher")!.Text = _vm.LauncherStatusText;
        this.FindControl<TextBlock>("SettingsDesktopEntry")!.Text = _vm.DesktopEntryStatusText;

        this.FindControl<TextBlock>("SettingsSteamStatus")!.Text = _vm.SteamStatusText;
        this.FindControl<TextBlock>("SettingsSteamType")!.Text = _vm.SteamTypeText;
        this.FindControl<TextBlock>("SettingsSteamInstallation")!.Text = _vm.SteamInstallationText;
        this.FindControl<TextBlock>("SettingsSteamAccount")!.Text = _vm.SteamAccountText;
        this.FindControl<TextBlock>("SettingsSteamShortcut")!.Text = _vm.SteamShortcutStatusText;
        this.FindControl<TextBlock>("SettingsSteamExecutable")!.Text = _vm.SteamShortcutExecutableText;
        this.FindControl<TextBlock>("SettingsSteamLastOperation")!.Text = _vm.SteamLastOperationText;
        var canChangeSteam = _vm.HasSteamAccount && !_vm.Busy;
        this.FindControl<Button>("SettingsSteamAddButton")!.IsEnabled = canChangeSteam && !_vm.SteamShortcutInstalled;
        this.FindControl<Button>("SettingsSteamRepairButton")!.IsEnabled = canChangeSteam;
        this.FindControl<Button>("SettingsSteamRemoveButton")!.IsEnabled = canChangeSteam;

        this.FindControl<TextBlock>("SettingsLastPreflight")!.Text = _vm.LastPreflightText;
        this.FindControl<TextBox>("DiagnosticsTextBox")!.Text = _vm.DiagnosticsReport;
        this.FindControl<TextBlock>("SettingsAboutVersion")!.Text = _vm.AppVersion;
        this.FindControl<TextBlock>("SettingsAboutPlatform")!.Text = _vm.PlatformText;
    }

    private static void ApplyCornerStyle(bool rounded)
    {
        if (Application.Current is null)
        {
            return;
        }
        var resources = Application.Current.Resources;
        resources["CornerCard"] = rounded ? new CornerRadius(10) : default;
        resources["CornerCardLeft"] = rounded ? new CornerRadius(9, 0, 0, 9) : default;
        resources["CornerInner"] = rounded ? new CornerRadius(6) : default;
        resources["CornerPill"] = rounded ? new CornerRadius(16) : default;
    }

    private void Settings_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HandleEscape())
        {
            e.Handled = true;
        }
    }

    /// <summary>Closes the topmost settings surface. Returns false when Settings is not open.</summary>
    public bool HandleEscape()
    {
        if (!IsVisible)
        {
            return false;
        }

        if (this.FindControl<Border>("ConfirmOverlay")!.IsVisible)
        {
            _confirmation?.TrySetResult(false);
            return true;
        }
        if (this.FindControl<Border>("RuntimeOverlay")!.IsVisible)
        {
            this.FindControl<Border>("RuntimeOverlay")!.IsVisible = false;
            return true;
        }
        if (this.FindControl<Border>("LogsOverlay")!.IsVisible)
        {
            this.FindControl<Border>("LogsOverlay")!.IsVisible = false;
            return true;
        }
        if (this.FindControl<Border>("SettingsOverlay")!.IsVisible)
        {
            CloseSettings();
            return true;
        }
        return false;
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => CloseSettings();

    private void RoundedCorners_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }
        var enabled = this.FindControl<ToggleSwitch>("RoundedCornersToggle")!.IsChecked == true;
        _vm.SetRoundedCorners(enabled);
        ApplyCornerStyle(enabled);
    }

    private void AppUpdates_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }
        _vm.SetAutoCheckForAppUpdates(this.FindControl<ToggleSwitch>("AppUpdatesToggle")!.IsChecked == true);
    }

    private void BackendUpdates_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }
        _vm.SetUpdateBackendAutomatically(this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked == true);
    }

    private void ConfirmSteamRestart_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }
        _vm.SetConfirmSteamRestart(this.FindControl<ToggleSwitch>("ConfirmSteamRestartToggle")!.IsChecked == true);
    }

    private void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }
        var tag = (this.FindControl<ComboBox>("LanguageCombo")!.SelectedItem as ComboBoxItem)?.Tag as string;
        _vm.SetLanguage(tag ?? "en");
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.CheckForUpdatesAsync();
        RefreshAll();
    }

    private void OpenRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.LatestReleaseUrl is { } url)
        {
            OpenUrl(url);
        }
    }

    private async void CheckBackend_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.CheckBackendUpdateAsync();
        RefreshAll();
    }

    private async void UpdateBackendSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var confirmed = await ConfirmAsync(
            Loc.Get("updateHelperTitle"),
            Loc.Get("updateHelperText"),
            Loc.Get("updateHelperNow"));
        if (!confirmed)
        {
            return;
        }
        await _vm.UpdateBackendAsync();
        RefreshAll();
    }

    private async void RepairWivrn_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.RepairDesktopAsync();
        RefreshAll();
    }

    private async void ReinstallGameEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ReinstallGameDesktopEntryAsync();
        RefreshAll();
    }

    private void ViewRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        this.FindControl<TextBox>("RuntimeInfoTextBox")!.Text = _vm.RuntimeDetailsText;
        this.FindControl<Border>("RuntimeOverlay")!.IsVisible = true;
    }

    private void CloseRuntime_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Border>("RuntimeOverlay")!.IsVisible = false;
    }

    private void OpenLauncherLog_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var path = _vm.LauncherLogPath;
        if (File.Exists(path))
        {
            OpenPath(path);
        }
        else
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            OpenPath(directory);
        }
    }

    private async void AddSteamSettings_Click(object? sender, RoutedEventArgs e)
    {
        await RunSteamOperationAsync(() => _vm!.AddOrUpdateSteamAsync(allowRestart: true),
            Loc.Get("addSteamTitle"), Loc.Get("addSteamText"), Loc.Get("restartSteamAdd"));
    }

    private async void RepairSteamSettings_Click(object? sender, RoutedEventArgs e)
    {
        await RunSteamOperationAsync(() => _vm!.AddOrUpdateSteamAsync(allowRestart: true),
            Loc.Get("repairSteamTitle"), Loc.Get("repairSteamText"), Loc.Get("restartSteamRepair"));
    }

    private async void RemoveSteamSettings_Click(object? sender, RoutedEventArgs e)
    {
        await RunSteamOperationAsync(() => _vm!.RemoveSteamShortcutAsync(allowRestart: true),
            Loc.Get("removeSteamTitle"), Loc.Get("removeSteamText"), Loc.Get("restartSteamRemove"));
    }

    private async Task RunSteamOperationAsync(Func<Task> operation, string title, string text, string okLabel)
    {
        if (_vm is null)
        {
            return;
        }
        if (!_vm.CanModifySteamNow(out var reason))
        {
            await ShowInfoAsync(Loc.Get("steamBlockedTitle"), reason ?? Loc.Get("steamBlockedText"));
            return;
        }
        if (_vm.IsSteamRunning && _vm.ConfirmSteamRestart)
        {
            var confirmed = await ConfirmAsync(title, text, okLabel);
            if (!confirmed)
            {
                return;
            }
        }
        await operation();
        RefreshAll();
    }

    private async void RunChecks_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.RunPreflightAsync();
        RefreshAll();
    }

    private void ViewLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        this.FindControl<TextBox>("LogsTextBox")!.Text = _vm.GetLatestLogText();
        this.FindControl<Border>("LogsOverlay")!.IsVisible = true;
    }

    private void CloseLogs_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Border>("LogsOverlay")!.IsVisible = false;
    }

    private void OpenLogDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        Directory.CreateDirectory(_vm.LogsDirectory);
        OpenPath(_vm.LogsDirectory);
    }

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        var report = new DataTransfer();
        report.Add(DataTransferItem.CreateText(_vm.DiagnosticsReport));
        await clipboard.SetDataAsync(report);
        var button = this.FindControl<Button>("CopyReportButton")!;
        button.Content = Loc.Get("copied");
        await Task.Delay(1200);
        button.Content = Loc.Get("copyDiagnosticReport");
    }

    private async void RestoreDefaults_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var wasGerman = _vm.Language == "de";
        var confirmed = await ConfirmAsync(
            Loc.Get("restoreDefaultsTitle"),
            Loc.Get("restoreDefaultsText"),
            Loc.Get("restoreDefaultsConfirm"));
        if (!confirmed)
        {
            return;
        }
        _vm.RestoreDefaults();
        ApplyCornerStyle(true);
        _loading = true;
        this.FindControl<ToggleSwitch>("RoundedCornersToggle")!.IsChecked = true;
        this.FindControl<ToggleSwitch>("AppUpdatesToggle")!.IsChecked = true;
        this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked = false;
        this.FindControl<ToggleSwitch>("ConfirmSteamRestartToggle")!.IsChecked = true;
        this.FindControl<ComboBox>("LanguageCombo")!.SelectedIndex = 0;
        _loading = false;
        RefreshAll();
        if (wasGerman)
        {
            await ShowInfoAsync(Loc.Get("restoreDefaultsTitle"), Loc.Get("restoreDefaultsRestart"));
        }
    }

    private void OpenLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            OpenUrl(url);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string text, string okLabel)
    {
        var overlay = ShowConfirmationSurface(title, text, okLabel);
        _confirmation = new TaskCompletionSource<bool>();
        var result = await _confirmation.Task;
        HideConfirmationSurface(overlay);
        return result;
    }

    private async Task ShowInfoAsync(string title, string text)
    {
        var overlay = ShowConfirmationSurface(title, text, Loc.Get("close"));
        this.FindControl<Button>("ConfirmCancelButton")!.IsVisible = false;
        _confirmation = new TaskCompletionSource<bool>();
        await _confirmation.Task;
        HideConfirmationSurface(overlay);
    }

    private Border ShowConfirmationSurface(string title, string text, string okLabel)
    {
        var overlay = this.FindControl<Border>("ConfirmOverlay")!;
        this.FindControl<TextBlock>("ConfirmTitle")!.Text = title;
        this.FindControl<TextBlock>("ConfirmText")!.Text = text;
        this.FindControl<Button>("ConfirmOkButton")!.Content = okLabel;
        this.FindControl<Button>("ConfirmCancelButton")!.IsVisible = true;
        overlay.IsVisible = true;
        SetBackgroundInteraction(enabled: false);
        // The dialog was measured while hidden. Force a fresh measure so both
        // the rendered panel and its hit-test bounds use the wrapped text height.
        overlay.InvalidateMeasure();
        overlay.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (overlay.IsVisible)
            {
                overlay.InvalidateMeasure();
                overlay.UpdateLayout();
                this.FindControl<Button>("ConfirmOkButton")!.Focus();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
        return overlay;
    }

    private void HideConfirmationSurface(Border overlay)
    {
        overlay.IsVisible = false;
        SetBackgroundInteraction(enabled: true);
        this.FindControl<Button>("SettingsCloseButton")!.Focus();
    }

    private void SetBackgroundInteraction(bool enabled)
    {
        this.FindControl<ScrollViewer>("SettingsScroll")!.IsEnabled = enabled;
        this.FindControl<Button>("SettingsCloseButton")!.IsEnabled = enabled;
    }

    private void ConfirmOk_Click(object? sender, RoutedEventArgs e) => _confirmation?.TrySetResult(true);

    private void ConfirmCancel_Click(object? sender, RoutedEventArgs e) => _confirmation?.TrySetResult(false);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { url },
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not open {url}: {ex.Message}");
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { path },
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not open {path}: {ex.Message}");
        }
    }
}
