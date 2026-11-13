using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

internal sealed class SettingsUiHost : IDisposable
{
    public SettingsUiHost(
        TemporaryHome home,
        Action<AppSettings>? configure = null,
        int width = 800,
        int height = 740)
    {
        var paths = home.Paths;
        var store = new SettingsStore(paths);
        var settings = store.Load();
        settings.Language = "en";
        settings.Theme = "dark";
        settings.UiScale = 100;
        settings.MotionPreference = "reduced";
        settings.RoundedCorners = true;
        settings.CheckForAppUpdates = false;
        settings.UpdateBackendAutomatically = false;
        settings.ConfirmSteamRestart = true;
        settings.SelectedToken = "latest";
        settings.SetupWizardCompleted = true;
        configure?.Invoke(settings);
        store.Save(settings);

        Loc.Use(settings.Language);
        AppearanceManager.Initialize(settings);

        ViewModel = new InstallerViewModel();
        View = new SettingsView();
        // Mirror MainWindow's RootScale so scale-dependent layout is exercised
        // by the headless Settings tests too.
        RootScale = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(
                AppearanceManager.ScaleFactor,
                AppearanceManager.ScaleFactor),
            Child = View,
        };
        Window = new Window
        {
            Width = width,
            Height = height,
            Content = RootScale,
        };

        Window.Show();
        HeadlessUi.Pump(120);
        View.Open(ViewModel);
        HeadlessUi.Pump(260);
    }

    public Window Window { get; }

    public LayoutTransformControl RootScale { get; }

    public SettingsView View { get; }

    public InstallerViewModel ViewModel { get; }

    public SettingsTabBar Tabs => View.FindControl<SettingsTabBar>("SettingsTabs")!;

    public ScrollViewer Pane(string name) => View.FindControl<ScrollViewer>(name)!;

    public Button TabButton(string name) => Tabs.FindControl<Button>(name)!;

    public void ClickTab(string name) =>
        TabButton(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public void Dispose() => Window.Close();
}
