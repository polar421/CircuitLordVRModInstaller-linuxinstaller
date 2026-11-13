using System.IO;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class GamePageTransitionUiTests : HeadlessUiTest
{
    [Fact]
    public Task TitanfallPageSurfaceOwnsChromeDuringOpenAndClose() => Dispatch(() =>
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

        // An existing checkout with the wrong origin makes initialization fail
        // immediately instead of cloning the real backend during the UI test.
        Directory.CreateDirectory(Path.Combine(Home.Paths.AppData, "backend", ".git"));

        var window = new MainWindow();
        window.Show();
        HeadlessUi.Pump(100);
        window.FindControl<Border>("AttributionOverlay")!.IsVisible = false;

        var installerPage = window.FindControl<Grid>("InstallerPage")!;
        var nav = window.FindControl<Grid>("InstallerNav")!;
        var tabs = window.FindControl<TitanfallTabBar>("InstallerTabs")!;
        var scroll = window.FindControl<ScrollViewer>("InstallerScroll")!;
        var back = window.FindControl<Button>("BackToGamesButton")!;
        var overview = tabs.FindControl<Button>("OverviewTabButton")!;
        var card = window.FindControl<Button>("TitanfallCard")!;

        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(installerPage.IsVisible);
        Assert.Equal(0.0, installerPage.Opacity);
        Assert.False(installerPage.IsHitTestVisible);
        HeadlessUi.Pump(1200);

        Assert.True(installerPage.IsEnabled);
        Assert.True(installerPage.IsHitTestVisible);
        Assert.Equal(1.0, installerPage.Opacity);
        Assert.Equal(AccessibilityView.Default, AutomationProperties.GetAccessibilityView(installerPage));
        AssertAutomationVisible(back);
        AssertAutomationVisible(overview);

        Assert.True(back.Focus());
        HeadlessUi.Pump(20);
        Assert.True(back.IsFocused);

        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // The close transition keeps the surface mounted for layout and
        // artwork measurement, but the entire page must already be inert and
        // removed from the accessibility tree.
        Assert.True(installerPage.IsVisible);
        Assert.False(installerPage.IsEnabled);
        Assert.False(installerPage.IsHitTestVisible);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(installerPage));
        Assert.False(back.IsEffectivelyEnabled);
        Assert.False(overview.IsEffectivelyEnabled);
        Assert.False(back.IsFocused);
        Assert.True(card.IsFocused);
        AssertAutomationHidden(nav);
        AssertAutomationHidden(tabs);
        AssertAutomationHidden(scroll);
        AssertAutomationHidden(back);
        AssertAutomationHidden(overview);

        HeadlessUi.Pump(600);
        Assert.False(installerPage.IsVisible);
        Assert.True(installerPage.IsEnabled);
        Assert.True(installerPage.IsHitTestVisible);
        Assert.Equal(1.0, installerPage.Opacity);
        Assert.Equal(AccessibilityView.Default, AutomationProperties.GetAccessibilityView(installerPage));
        window.Close();
    });

    [Theory]
    [InlineData("full")]
    [InlineData("reduced")]
    public Task BackDuringOpenSettlesOnGameSelection(string motion) => Dispatch(() =>
    {
        var window = CreateWindow(motion);
        try
        {
            var page = window.FindControl<Grid>("InstallerPage")!;
            var game = window.FindControl<ScrollViewer>("GameScroll")!;
            var card = window.FindControl<Button>("TitanfallCard")!;
            var back = window.FindControl<Button>("BackToGamesButton")!;

            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (motion == "full")
            {
                HeadlessUi.Pump(300);
            }
            Assert.True(page.IsVisible);
            Assert.True(page.IsEnabled);
            Assert.True(back.IsEffectivelyEnabled);

            // Back is interactive while the page is fully opaque but its
            // content reveal is still running. It must reverse the open.
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(500);

            Assert.False(page.IsVisible);
            Assert.True(game.IsVisible);
            Assert.Equal(1.0, game.Opacity);
            Assert.True(card.IsFocused);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData("full")]
    [InlineData("reduced")]
    public Task ChoosingGameDuringCloseSettlesOnTitanfallPage(string motion) => Dispatch(() =>
    {
        var window = CreateWindow(motion);
        try
        {
            var page = window.FindControl<Grid>("InstallerPage")!;
            var game = window.FindControl<ScrollViewer>("GameScroll")!;
            var card = window.FindControl<Button>("TitanfallCard")!;
            var back = window.FindControl<Button>("BackToGamesButton")!;

            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(1200);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (motion == "full")
            {
                HeadlessUi.Pump(300);
                Assert.True(game.IsHitTestVisible);
            }
            Assert.False(page.IsEnabled);

            // The selection surface is clickable during the close, so choosing
            // Titanfall again must reverse the close instead of being ignored.
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(500);

            Assert.True(page.IsVisible);
            Assert.True(page.IsEnabled);
            Assert.True(page.IsHitTestVisible);
            Assert.Equal(1.0, page.Opacity);
            Assert.False(game.IsVisible);
            Assert.True(back.IsFocused);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Back)]
    public Task OverviewBackKeysReturnToSelectionAndFocusCard(Key key) => Dispatch(() =>
    {
        var window = CreateWindow();
        try
        {
            var page = window.FindControl<Grid>("InstallerPage")!;
            var card = window.FindControl<Button>("TitanfallCard")!;
            var back = window.FindControl<Button>("BackToGamesButton")!;

            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(1200);
            Assert.True(back.Focus());
            HeadlessUi.Pump(20);

            back.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
            });
            HeadlessUi.Pump(600);

            Assert.False(page.IsVisible);
            Assert.True(card.IsFocused);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task RepeatedReversalSettlesOnLatestRequest() => Dispatch(() =>
    {
        var window = CreateWindow();
        try
        {
            var page = window.FindControl<Grid>("InstallerPage")!;
            var game = window.FindControl<ScrollViewer>("GameScroll")!;
            var card = window.FindControl<Button>("TitanfallCard")!;
            var back = window.FindControl<Button>("BackToGamesButton")!;

            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(40);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(40);
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(40);
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(700);

            Assert.False(page.IsVisible);
            Assert.True(game.IsVisible);
            Assert.True(card.IsFocused);
        }
        finally
        {
            window.Close();
        }
    });

    private MainWindow CreateWindow(string motion = "full")
    {
        var store = new SettingsStore(Home.Paths);
        var settings = store.Load();
        settings.Language = "en";
        settings.Theme = "dark";
        settings.UiScale = 100;
        settings.MotionPreference = motion;
        settings.CheckForAppUpdates = false;
        settings.UpdateBackendAutomatically = false;
        settings.SetupWizardCompleted = true;
        store.Save(settings);
        Loc.Use(settings.Language);
        AppearanceManager.Initialize(settings);

        Directory.CreateDirectory(Path.Combine(Home.Paths.AppData, "backend", ".git"));

        var window = new MainWindow();
        window.Show();
        HeadlessUi.Pump(100);
        window.FindControl<Border>("AttributionOverlay")!.IsVisible = false;
        return window;
    }

    private static void AssertAutomationVisible(Control control)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        Assert.True(peer.IsControlElement());
        Assert.True(peer.IsContentElement());
        Assert.True(peer.IsEnabled());
    }

    private static void AssertAutomationHidden(Control control)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        Assert.False(peer.IsControlElement());
        Assert.False(peer.IsContentElement());
        Assert.False(peer.IsEnabled());
    }
}
