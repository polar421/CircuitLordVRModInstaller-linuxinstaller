using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class GamePageTransitionUiTests : HeadlessUiTest
{
    [Fact]
    public Task BackFromTitanfallHidesTheTabStripBeforeTheClosingAnimationEnds() => Dispatch(() =>
    {
        var store = new SettingsStore(Home.Paths);
        var settings = store.Load();
        settings.Language = "en";
        settings.Theme = "dark";
        settings.UiScale = 100;
        settings.MotionPreference = "full";
        settings.CheckForAppUpdates = false;
        settings.UpdateBackendAutomatically = false;
        settings.SetupWizardCompleted = true;
        store.Save(settings);
        Loc.Use(settings.Language);
        AppearanceManager.Initialize(settings);

        var window = new MainWindow();
        window.FindControl<Button>("TitanfallCard")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessUi.Pump(1200);

        var installerPage = window.FindControl<Grid>("InstallerPage")!;
        var tabs = window.FindControl<Control>("InstallerTabs")!;
        Assert.True(installerPage.IsVisible);
        Assert.Equal(1.0, tabs.Opacity);

        window.FindControl<Button>("BackToGamesButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // The close transition leaves the installer page visible while its
        // scroll content fades out, but the fixed tab strip must already be gone.
        Assert.Equal(0.0, tabs.Opacity);
        Assert.False(tabs.IsHitTestVisible);

        HeadlessUi.Pump(600);
        window.Close();
    });
}
