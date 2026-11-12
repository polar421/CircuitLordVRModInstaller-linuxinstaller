using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Appearance;
using CircuitLord.VRModInstaller.Core.Updates;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// The settings panel is deliberately self-contained: it owns only presentation,
/// while every action is delegated to <see cref="InstallerViewModel"/>.
/// </summary>
public partial class SettingsView : UserControl
{
    private static readonly TimeSpan OpenBackdropDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan OpenDialogDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan OpenTransformDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CloseBackdropDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan CloseDialogDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CloseTransformDuration = TimeSpan.FromMilliseconds(160);

    private InstallerViewModel? _vm;
    private TaskCompletionSource<bool>? _confirmation;
    private bool _loading;
    private SettingsAnimationState _settingsState;
    private CancellationTokenSource? _settingsAnimationCts;
    private long _settingsAnimationVersion;

    public SettingsView()
    {
        InitializeComponent();
        AppearanceManager.Theme.Changed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateThemeStatus);
        AppearanceManager.MotionChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyMotionPreference);
    }

    public event EventHandler? Closed;

    public void Open(InstallerViewModel vm)
    {
        // Repeated clicks while the sheet is opening or open must not create
        // another overlay or restart the entrance animation.
        if (_settingsState is SettingsAnimationState.Opening or SettingsAnimationState.Open)
        {
            return;
        }

        var reopening = _settingsState == SettingsAnimationState.Closing;
        CancelSettingsAnimation();
        var cts = new CancellationTokenSource();
        _settingsAnimationCts = cts;
        var generation = ++_settingsAnimationVersion;
        _settingsState = SettingsAnimationState.Opening;

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
        var overlay = this.FindControl<Border>("SettingsOverlay")!;
        var backdrop = this.FindControl<Border>("SettingsBackdrop")!;
        var dialog = this.FindControl<Border>("SettingsDialog")!;
        overlay.IsVisible = true;
        IsVisible = true;
        this.FindControl<Border>("RuntimeOverlay")!.IsVisible = false;
        this.FindControl<Border>("LogsOverlay")!.IsVisible = false;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = false;
        if (!reopening)
        {
            // Start from the current hidden state. This is also what makes the
            // animation deterministic if a previous close was interrupted.
            ModalTransitions.Configure(backdrop, dialog, animate: false,
                OpenBackdropDuration, OpenDialogDuration, OpenTransformDuration);
            ModalTransitions.SetInitial(backdrop, dialog);
        }
        this.FindControl<ToggleSwitch>("RoundedCornersToggle")!.IsChecked = vm.RoundedCorners;
        this.FindControl<ToggleSwitch>("AppUpdatesToggle")!.IsChecked = vm.AutoCheckForAppUpdates;
        this.FindControl<ComboBox>("UpdateChannelCombo")!.SelectedIndex = UpdateChannelIndex(vm.UpdateChannelSetting);
        this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked = vm.UpdateBackendAutomatically;
        this.FindControl<ToggleSwitch>("ConfirmSteamRestartToggle")!.IsChecked = vm.ConfirmSteamRestart;
        this.FindControl<ComboBox>("ThemeCombo")!.SelectedIndex = vm.ThemeSetting switch
        {
            "system" => 0,
            "light" => 2,
            _ => 1,
        };
        this.FindControl<ComboBox>("MotionCombo")!.SelectedIndex = vm.MotionSetting switch
        {
            "reduced" => 1,
            "full" => 2,
            _ => 0,
        };
        this.FindControl<Slider>("UiScaleSlider")!.Value = vm.UiScalePercent;
        this.FindControl<ComboBox>("LanguageCombo")!.SelectedIndex = LanguageIndex(vm.Language);
        UpdateUiScaleLabel(vm.UiScalePercent);
        UpdateThemeStatus();
        this.FindControl<ScrollViewer>("SettingsScroll")!.Offset = new Vector(0, 0);
        _loading = false;
        RefreshAll();

        var animated = AppearanceManager.AnimationsEnabled;
        ModalTransitions.Configure(backdrop, dialog, animated,
            OpenBackdropDuration, OpenDialogDuration, OpenTransformDuration);
        if (animated)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = StartOpenAnimationAsync(backdrop, dialog, cts, generation),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            ModalTransitions.ApplyOpen(backdrop, dialog);
            _settingsState = SettingsAnimationState.Open;
        }

        // The overlay is only becoming visible in this pass. Move keyboard
        // focus after layout so it cannot remain on the title-bar gear behind
        // the modal sheet.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible && _settingsState is SettingsAnimationState.Opening or SettingsAnimationState.Open)
                {
                    // Move through the sheet itself first; Avalonia otherwise
                    // leaves the old title-bar focus adorner painted until the
                    // next keyboard navigation.
                    Focus();
                    this.FindControl<Button>("SettingsCloseButton")!.Focus();
                }
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async Task StartOpenAnimationAsync(
        Border backdrop, Border dialog, CancellationTokenSource cts, long generation)
    {
        try
        {
            if (generation != _settingsAnimationVersion
                || _settingsState != SettingsAnimationState.Opening)
            {
                return;
            }
            ModalTransitions.ApplyOpen(backdrop, dialog);
            await Task.Delay(OpenTransformDuration, cts.Token);
            if (generation == _settingsAnimationVersion
                && _settingsState == SettingsAnimationState.Opening)
            {
                _settingsState = SettingsAnimationState.Open;
            }
        }
        catch (OperationCanceledException)
        {
            // A close or reduced-motion change interrupted the entrance.
        }
    }

    private void CloseSettings()
    {
        if (_settingsState is SettingsAnimationState.Closed or SettingsAnimationState.Closing)
        {
            return;
        }

        _settingsState = SettingsAnimationState.Closing;
        CancelSettingsAnimation();
        var cts = new CancellationTokenSource();
        _settingsAnimationCts = cts;
        var generation = ++_settingsAnimationVersion;
        var backdrop = this.FindControl<Border>("SettingsBackdrop")!;
        var dialog = this.FindControl<Border>("SettingsDialog")!;
        var animated = AppearanceManager.AnimationsEnabled;
        ModalTransitions.Configure(backdrop, dialog, animated,
            CloseBackdropDuration, CloseDialogDuration, CloseTransformDuration);
        ModalTransitions.ApplyClosed(backdrop, dialog);
        if (!animated)
        {
            FinishClose();
            return;
        }

        _ = FinishCloseAfterAsync(cts, generation);
    }

    private async Task FinishCloseAfterAsync(CancellationTokenSource cts, long generation)
    {
        try
        {
            await Task.Delay(CloseTransformDuration, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation == _settingsAnimationVersion
            && _settingsState == SettingsAnimationState.Closing)
        {
            FinishClose();
        }
    }

    private void FinishClose()
    {
        _settingsState = SettingsAnimationState.Closed;
        _settingsAnimationCts?.Dispose();
        _settingsAnimationCts = null;
        var overlay = this.FindControl<Border>("SettingsOverlay")!;
        var backdrop = this.FindControl<Border>("SettingsBackdrop")!;
        var dialog = this.FindControl<Border>("SettingsDialog")!;
        overlay.IsVisible = false;
        IsVisible = false;
        ModalTransitions.ResetHidden(backdrop, dialog);
        if (_vm is not null)
        {
            _vm.Changed -= ViewModel_Changed;
            _vm = null;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelSettingsAnimation()
    {
        _settingsAnimationCts?.Cancel();
        _settingsAnimationCts?.Dispose();
        _settingsAnimationCts = null;
        _settingsAnimationVersion++;
    }

    private void ApplyMotionPreference()
    {
        if (AppearanceManager.AnimationsEnabled
            || _settingsState == SettingsAnimationState.Closed)
        {
            return;
        }

        var backdrop = this.FindControl<Border>("SettingsBackdrop")!;
        var dialog = this.FindControl<Border>("SettingsDialog")!;
        CancelSettingsAnimation();
        ModalTransitions.Configure(backdrop, dialog, animate: false,
            CloseBackdropDuration, CloseDialogDuration, CloseTransformDuration);
        switch (_settingsState)
        {
            case SettingsAnimationState.Opening:
                ModalTransitions.ApplyOpen(backdrop, dialog);
                _settingsState = SettingsAnimationState.Open;
                break;
            case SettingsAnimationState.Closing:
                ModalTransitions.ApplyClosed(backdrop, dialog);
                FinishClose();
                break;
            default:
                ModalTransitions.ApplyOpen(backdrop, dialog);
                break;
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
        this.FindControl<ProgressBar>("SettingsAppUpdateProgress")!.IsVisible = _vm.AppUpdateInProgress;
        this.FindControl<Button>("SettingsCheckAppButton")!.IsEnabled = !_vm.AppUpdateInProgress;
        this.FindControl<Button>("SettingsInstallAppUpdateButton")!.IsVisible = _vm.UpdateAvailable && !_vm.AppUpdateInProgress;
        this.FindControl<Button>("SettingsInstallAppUpdateButton")!.IsEnabled = _vm.CanInstallAppUpdate;
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
        RefreshSelectedComboText();
        UpdateUiScaleLabel(_vm.UiScalePercent);
        UpdateThemeStatus();
    }

    private void RefreshSelectedComboText()
    {
        // Avalonia's ComboBox keeps a snapshot of the selected item's content.
        // Re-select it after a live language change so the closed box updates
        // together with the popup items; guard the handlers from persisting it.
        var wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (var name in new[] { "ThemeCombo", "MotionCombo", "UpdateChannelCombo" })
            {
                var combo = this.FindControl<ComboBox>(name);
                var index = combo?.SelectedIndex ?? -1;
                if (combo is not null && index >= 0)
                {
                    combo.SelectedIndex = -1;
                    combo.SelectedIndex = index;
                }
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static int UpdateChannelIndex(string channel) => channel switch
    {
        ReleaseChannelPreference.Stable => 1,
        ReleaseChannelPreference.Beta => 2,
        _ => 0,
    };

    private static int LanguageIndex(string language)
    {
        for (var index = 0; index < Loc.SupportedLanguages.Count; index++)
        {
            if (string.Equals(Loc.SupportedLanguages[index].Code, language, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private void UpdateUiScaleLabel(int percent)
    {
        this.FindControl<TextBlock>("UiScaleValue")!.Text =
            (percent / 100.0).ToString("P0", CultureInfo.CurrentCulture);
    }

    private void UpdateThemeStatus()
    {
        var block = this.FindControl<TextBlock>("SettingsThemeStatus");
        if (block is null)
        {
            return;
        }

        if (_vm?.ThemeSetting != "system")
        {
            block.IsVisible = false;
            return;
        }

        var theme = AppearanceManager.Theme;
        if (theme.SystemTheme.Detected)
        {
            var name = theme.SystemTheme.Theme == ResolvedTheme.Light
                ? Loc.Get("themeLight")
                : Loc.Get("themeDark");
            block.Text = Loc.Format("themeSystemFollowing", name);
        }
        else
        {
            block.Text = Loc.Get("themeSystemFallback");
        }
        block.IsVisible = true;
    }

    private void Theme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }

        var tag = (this.FindControl<ComboBox>("ThemeCombo")!.SelectedItem as ComboBoxItem)?.Tag as string;
        _vm.SetTheme(tag ?? "dark");
        UpdateThemeStatus();
    }

    private void Motion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }

        var tag = (this.FindControl<ComboBox>("MotionCombo")!.SelectedItem as ComboBoxItem)?.Tag as string;
        _vm.SetMotion(tag ?? "system");
    }

    private void UiScale_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var percent = Core.Appearance.UiScale.ClampPercent(e.NewValue);
        UpdateUiScaleLabel(percent);
        if (!_loading && _vm is not null)
        {
            _vm.SetUiScale(percent);
        }
    }

    private void UiScalePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string raw } || !int.TryParse(raw, out var percent))
        {
            return;
        }

        this.FindControl<Slider>("UiScaleSlider")!.Value = percent;
        UpdateUiScaleLabel(percent);
        _vm?.SetUiScale(percent);
    }

    private void UiScaleReset_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Slider>("UiScaleSlider")!.Value = UiScale.DefaultPercent;
        UpdateUiScaleLabel(UiScale.DefaultPercent);
        _vm?.SetUiScale(UiScale.DefaultPercent);
    }

    private static void ApplyCornerStyle(bool rounded)
    {
        if (Application.Current is null)
        {
            return;
        }
        var resources = Application.Current.Resources;
        resources["CornerWindow"] = rounded ? new CornerRadius(12) : default;
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

    private async void UpdateChannel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm is null)
        {
            return;
        }
        var tag = (this.FindControl<ComboBox>("UpdateChannelCombo")!.SelectedItem as ComboBoxItem)?.Tag as string;
        _vm.SetUpdateChannel(tag ?? ReleaseChannelPreference.Automatic);
        await _vm.CheckForUpdatesAsync();
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

    private async void InstallAppUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.InstallAppUpdateAsync();
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
        this.FindControl<ComboBox>("UpdateChannelCombo")!.SelectedIndex = 0;
        this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked = false;
        this.FindControl<ToggleSwitch>("ConfirmSteamRestartToggle")!.IsChecked = true;
        this.FindControl<ComboBox>("ThemeCombo")!.SelectedIndex = 1;
        this.FindControl<ComboBox>("MotionCombo")!.SelectedIndex = 0;
        this.FindControl<Slider>("UiScaleSlider")!.Value = UiScale.DefaultPercent;
        this.FindControl<ComboBox>("LanguageCombo")!.SelectedIndex = 0;
        _loading = false;
        RefreshAll();
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
        this.FindControl<ScrollViewer>("ConfirmScroll")?.SetCurrentValue(
            ScrollViewer.OffsetProperty, default);
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

    private enum SettingsAnimationState
    {
        Closed,
        Opening,
        Open,
        Closing,
    }
}
