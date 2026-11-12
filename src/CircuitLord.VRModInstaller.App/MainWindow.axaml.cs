using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

public partial class MainWindow : Window
{
    // Same destination as the original Windows installer (MainWindow.xaml.cs).
    private const string SupportUrl = "https://ko-fi.com/circuitlord";

    private readonly InstallerViewModel _vm = new();
    private TaskCompletionSource<bool>? _confirmation;
    private bool _updatingVersions;
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CircuitLordVRModInstaller/Assets/app-256.png")));
        ApplyCornerStyle(_vm.RoundedCorners);
        DataContext = _vm;
        _vm.Changed += (_, _) => RefreshUi();
        Opened += async (_, _) =>
        {
            await _vm.InitializeAsync();
            RefreshUi();
        };
    }

    private void RefreshUi()
    {
        this.FindControl<TextBlock>("InstalledText")!.Text = _vm.InstalledText;
        this.FindControl<TextBlock>("BackendText")!.Text = _vm.BackendText;
        this.FindControl<TextBlock>("CatalogueText")!.Text = _vm.CatalogueText;
        this.FindControl<TextBlock>("GameText")!.Text = _vm.GameText;
        this.FindControl<TextBlock>("PreflightText")!.Text = _vm.PreflightText;
        this.FindControl<TextBlock>("DesktopText")!.Text = _vm.DesktopText;
        this.FindControl<TextBlock>("SteamText")!.Text = _vm.SteamText;
        this.FindControl<TextBlock>("RuntimeText")!.Text = _vm.RuntimeText;
        this.FindControl<TextBlock>("ManifestText")!.Text = _vm.ManifestText;
        this.FindControl<TextBlock>("StageText")!.Text = _vm.StageText;
        this.FindControl<TextBlock>("TitanfallStatus")!.Text = _vm.IsInstalled
            ? Loc.Format("installedStatus", _vm.InstalledText)
            : Loc.Get("readyStatus");

        this.FindControl<ProgressBar>("Progress")!.IsVisible = _vm.Busy;
        this.FindControl<Button>("InstallButton")!.Content = _vm.PrimaryActionLabel;
        this.FindControl<Button>("InstallButton")!.IsEnabled = _vm.CanInstall && !_vm.Busy;
        this.FindControl<Button>("LaunchButton")!.IsEnabled = _vm.CanLaunch && !_vm.Busy;
        this.FindControl<Button>("UninstallButton")!.IsEnabled = _vm.CanUninstall && !_vm.Busy;
        this.FindControl<Button>("SteamButton")!.IsEnabled = _vm.HasSteamAccount && !_vm.Busy;
        this.FindControl<Button>("SteamRemoveButton")!.IsEnabled = _vm.HasSteamAccount && !_vm.Busy;

        var combo = this.FindControl<ComboBox>("VersionCombo")!;
        _updatingVersions = true;
        combo.ItemsSource = _vm.Versions;
        combo.SelectedItem = _vm.SelectedVersion;
        _updatingVersions = false;

        this.FindControl<ListBox>("LogsList")!.ItemsSource = _vm.LogLines;
        this.FindControl<ItemsControl>("ChangelogList")!.ItemsSource = _vm.ChangelogLines;
        this.FindControl<TextBlock>("UpdateStatusText")!.Text = _vm.UpdateStatusText;
        this.FindControl<Button>("OpenReleaseButton")!.IsEnabled = _vm.LatestReleaseUrl is not null;
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

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ResizeNorth_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.North, e);
    private void ResizeSouth_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.South, e);
    private void ResizeWest_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.West, e);
    private void ResizeEast_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.East, e);
    private void ResizeNorthWest_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.NorthWest, e);
    private void ResizeNorthEast_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.NorthEast, e);
    private void ResizeSouthWest_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.SouthWest, e);
    private void ResizeSouthEast_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.SouthEast, e);

    private void TryResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void SelectTitanfall_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<StackPanel>("GameSelect")!.IsVisible = false;
        this.FindControl<StackPanel>("GameFooter")!.IsVisible = false;
        this.FindControl<StackPanel>("InstallerView")!.IsVisible = true;
        RefreshUi();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<StackPanel>("GameSelect")!.IsVisible = true;
        this.FindControl<StackPanel>("GameFooter")!.IsVisible = true;
        this.FindControl<StackPanel>("InstallerView")!.IsVisible = false;
    }

    private void OpenSupport_Click(object? sender, RoutedEventArgs e) => OpenUrl(SupportUrl);

    private void OpenUrl(string url)
    {
        // Desktop-standard URL opening; never spawns a terminal and does not claim success.
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

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        this.FindControl<ToggleSwitch>("RoundedCornersToggle")!.IsChecked = _vm.RoundedCorners;
        this.FindControl<ToggleSwitch>("AppUpdatesToggle")!.IsChecked = _vm.AutoCheckForAppUpdates;
        this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked = _vm.UpdateBackendAutomatically;
        this.FindControl<ComboBox>("LanguageCombo")!.SelectedIndex = _vm.Language == "de" ? 1 : 0;
        this.FindControl<TextBlock>("UpdateStatusText")!.Text = _vm.UpdateStatusText;
        _loadingSettings = false;
        this.FindControl<Border>("SettingsOverlay")!.IsVisible = true;
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Border>("SettingsOverlay")!.IsVisible = false;
    }

    private void RoundedCorners_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        var enabled = this.FindControl<ToggleSwitch>("RoundedCornersToggle")!.IsChecked == true;
        _vm.SetRoundedCorners(enabled);
        ApplyCornerStyle(enabled);
    }

    private void AppUpdates_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        _vm.SetAutoCheckForAppUpdates(this.FindControl<ToggleSwitch>("AppUpdatesToggle")!.IsChecked == true);
    }

    private void BackendUpdates_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        _vm.SetUpdateBackendAutomatically(this.FindControl<ToggleSwitch>("BackendUpdatesToggle")!.IsChecked == true);
    }

    private void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }
        var tag = (this.FindControl<ComboBox>("LanguageCombo")!.SelectedItem as ComboBoxItem)?.Tag as string;
        _vm.SetLanguage(tag ?? "en");
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.CheckForUpdatesAsync();
        RefreshUi();
    }

    private void OpenRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.LatestReleaseUrl is { } url)
        {
            OpenUrl(url);
        }
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.InstallSelectedAsync();
        RefreshUi();
    }

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.RunPreflightAsync();
        RefreshUi();
    }

    private async void UpdateBackend_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.UpdateBackendAsync();
        RefreshUi();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        await Task.Run(_vm.RefreshLocalState);
        await _vm.RefreshCatalogueAsync();
        RefreshUi();
    }

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmAsync(
            Loc.Get("uninstallTitle"),
            Loc.Get("uninstallText"),
            Loc.Get("uninstall"));
        if (!confirmed)
        {
            return;
        }
        await _vm.UninstallAsync();
        RefreshUi();
    }

    private void Launch_Click(object? sender, RoutedEventArgs e)
    {
        _vm.LaunchVr();
        RefreshUi();
    }

    private async void RepairDesktop_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.RepairDesktopAsync();
        RefreshUi();
    }

    private async void AddSteam_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsSteamRunning)
        {
            var confirmed = await ConfirmAsync(
                Loc.Get("addSteamTitle"),
                Loc.Get("addSteamText"),
                Loc.Get("restartSteamAdd"));
            if (!confirmed)
            {
                return;
            }
        }

        await _vm.AddOrUpdateSteamAsync(allowRestart: true);
        RefreshUi();
    }

    private async void RemoveSteam_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsSteamRunning)
        {
            var confirmed = await ConfirmAsync(
                Loc.Get("removeSteamTitle"),
                Loc.Get("removeSteamText"),
                Loc.Get("restartSteamRemove"));
            if (!confirmed)
            {
                return;
            }
        }
        await _vm.RemoveSteamShortcutAsync(allowRestart: true);
        RefreshUi();
    }

    private void Version_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingVersions)
        {
            return;
        }
        if (sender is ComboBox combo && combo.SelectedItem is VersionOption option)
        {
            _vm.SelectVersion(option);
        }
    }

    private void Changelog_Click(object? sender, PointerPressedEventArgs e)
    {
        this.FindControl<Border>("ChangelogOverlay")!.IsVisible = true;
    }

    private void CloseChangelog_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Border>("ChangelogOverlay")!.IsVisible = false;
    }

    private async Task<bool> ConfirmAsync(string title, string text, string okLabel)
    {
        this.FindControl<TextBlock>("ConfirmTitle")!.Text = title;
        this.FindControl<TextBlock>("ConfirmText")!.Text = text;
        this.FindControl<Button>("ConfirmOkButton")!.Content = okLabel;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = true;

        _confirmation = new TaskCompletionSource<bool>();
        var result = await _confirmation.Task;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = false;
        return result;
    }

    private void ConfirmOk_Click(object? sender, RoutedEventArgs e) => _confirmation?.TrySetResult(true);

    private void ConfirmCancel_Click(object? sender, RoutedEventArgs e) => _confirmation?.TrySetResult(false);
}
