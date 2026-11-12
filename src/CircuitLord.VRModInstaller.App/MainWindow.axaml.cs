using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace CircuitLord.VRModInstaller;

public partial class MainWindow : Window
{
    private readonly InstallerViewModel _vm = new();
    private TaskCompletionSource<bool>? _confirmation;
    private bool _updatingVersions;

    public MainWindow()
    {
        InitializeComponent();
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
            ? "Installed · " + _vm.InstalledText
            : "Ready to install through tf2vr-linux";

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
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void SelectTitanfall_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<StackPanel>("GameSelect")!.IsVisible = false;
        this.FindControl<StackPanel>("InstallerView")!.IsVisible = true;
        RefreshUi();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<StackPanel>("GameSelect")!.IsVisible = true;
        this.FindControl<StackPanel>("InstallerView")!.IsVisible = false;
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
            "Uninstall Titanfall 2 VR?",
            "The backend removes the mod files, its launcher and Proton-TF2VR. Campaign saves and the normal Titanfall 2 installation are kept.",
            "Uninstall");
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
                "Add Titanfall 2 VR to Steam?",
                "Steam needs to restart to install or update the library shortcut. The application will close Steam gracefully, update the shortcut and reopen the same Steam installation automatically. Existing games and shortcuts are preserved.",
                "Restart Steam and Add");
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
                "Remove the Titanfall 2 VR shortcut?",
                "Steam will be closed gracefully, the Titanfall 2 VR shortcut removed, and Steam reopened. Other shortcuts are preserved.",
                "Restart Steam and Remove");
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
