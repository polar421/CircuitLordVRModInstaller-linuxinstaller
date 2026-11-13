using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class SettingsTabUiTests : HeadlessUiTest
{
    private static readonly (SettingsTab Tab, string Button, string Pane)[] SettingsTabs =
    [
        (SettingsTab.General, "SettingsGeneralTabButton", "SettingsGeneralScroll"),
        (SettingsTab.Appearance, "SettingsAppearanceTabButton", "SettingsAppearanceScroll"),
        (SettingsTab.Diagnostics, "SettingsDiagnosticsTabButton", "SettingsDiagnosticsScroll"),
        (SettingsTab.About, "SettingsAboutTabButton", "SettingsAboutScroll"),
    ];

    private static readonly string[] Languages =
    [
        "en", "de", "es", "fr", "pt-BR", "it", "pl", "ru", "ja", "zh-Hans",
    ];

    [Fact]
    public Task TabSwitchingShowsTheSelectedPane() => Dispatch(() =>
    {
        using var ui = new SettingsUiHost(Home);

        Assert.True(ui.View.FindControl<Border>("SettingsOverlay")!.IsVisible);
        AssertPane(ui, SettingsTab.General);
        Assert.True(ui.View.FindControl<Button>("SettingsRestoreButton")!.IsVisible);

        foreach (var tab in SettingsTabs)
        {
            ui.ClickTab(tab.Button);
            HeadlessUi.Pump(160);
            AssertPane(ui, tab.Tab);
        }

        ui.TabButton("SettingsAboutTabButton").Focus();
        HeadlessUi.Pump(60);
        ui.TabButton("SettingsAboutTabButton").RaiseEvent(KeyEvent(Key.Home));
        HeadlessUi.Pump(120);
        AssertPane(ui, SettingsTab.General);

        ui.TabButton("SettingsGeneralTabButton").RaiseEvent(KeyEvent(Key.Left));
        HeadlessUi.Pump(120);
        AssertPane(ui, SettingsTab.About);

        ui.View.FindControl<Button>("SettingsCloseButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessUi.Pump(220);
        Assert.False(ui.View.IsVisible);

        ui.View.Open(ui.ViewModel);
        HeadlessUi.Pump(260);
        AssertPane(ui, SettingsTab.General);
    });

    [Fact]
    public Task EachSettingsPaneKeepsItsOwnScrollOffset() => Dispatch(() =>
    {
        using var ui = new SettingsUiHost(Home);
        ui.Window.Height = 540;
        HeadlessUi.Pump(180);

        var offsets = new Dictionary<SettingsTab, double>();
        for (var index = 0; index < SettingsTabs.Length; index++)
        {
            var tab = SettingsTabs[index];
            ui.ClickTab(tab.Button);
            HeadlessUi.Pump(160);

            var pane = ui.Pane(tab.Pane);
            var max = Math.Max(0, pane.Extent.Height - pane.Viewport.Height);
            Assert.True(max > 10, $"{tab.Tab} pane should be scrollable");

            var requested = 40 + index * 35;
            var expected = Math.Min(requested, max);
            pane.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, expected));
            HeadlessUi.Pump(90);

            offsets[tab.Tab] = pane.Offset.Y;
            Assert.True(pane.Offset.Y > 0, $"{tab.Tab} pane should accept a scroll offset");
        }

        foreach (var tab in SettingsTabs)
        {
            ui.ClickTab(tab.Button);
            HeadlessUi.Pump(180);
            Assert.InRange(
                ui.Pane(tab.Pane).Offset.Y,
                offsets[tab.Tab] - 4,
                offsets[tab.Tab] + 4);
        }
    });

    [Fact]
    public Task LanguageChangesPreserveTheActiveSettingsTab() => Dispatch(() =>
    {
        using var ui = new SettingsUiHost(Home);
        ui.ClickTab("SettingsAboutTabButton");
        HeadlessUi.Pump(160);

        foreach (var language in Languages)
        {
            Loc.Use(language);
            HeadlessUi.Pump(120);

            Assert.Equal(Loc.Get("general"), Label(ui, "SettingsGeneralTabButton"));
            Assert.Equal(Loc.Get("appearance"), Label(ui, "SettingsAppearanceTabButton"));
            Assert.Equal(Loc.Get("diagnostics"), Label(ui, "SettingsDiagnosticsTabButton"));
            Assert.Equal(Loc.Get("about"), Label(ui, "SettingsAboutTabButton"));
            AssertPane(ui, SettingsTab.About);
        }

        Loc.Use("en");
        HeadlessUi.Pump(100);
    });

    [Fact]
    public Task RestoreDefaultsRequiresConfirmationAndResetsOnlyPreferences() => Dispatch(() =>
    {
        using var ui = new SettingsUiHost(Home, settings =>
        {
            settings.Language = "de";
            settings.Theme = "light";
            settings.UiScale = 125;
            settings.RoundedCorners = false;
            settings.MotionPreference = "reduced";
            settings.SelectedToken = "beta";
            settings.SetupWizardCompleted = true;
        });

        Assert.True(ui.View.FindControl<Border>("SettingsOverlay")!.IsVisible);
        ui.ClickTab("SettingsAboutTabButton");
        HeadlessUi.Pump(160);
        Assert.Equal("de", ui.ViewModel.Language);
        Assert.Equal("light", ui.ViewModel.ThemeSetting);
        Assert.Equal(125, ui.ViewModel.UiScalePercent);
        Assert.False(ui.ViewModel.RoundedCorners);

        var restore = ui.View.FindControl<Button>("SettingsRestoreButton")!;
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessUi.Pump(140);

        var confirmation = ui.View.FindControl<Border>("ConfirmOverlay")!;
        Assert.True(confirmation.IsVisible);

        ui.View.FindControl<Button>("ConfirmCancelButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessUi.Pump(180);
        Assert.False(confirmation.IsVisible);
        Assert.Equal("de", ui.ViewModel.Language);
        Assert.Equal(125, ui.ViewModel.UiScalePercent);

        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessUi.Pump(140);
        Assert.True(confirmation.IsVisible);
        ui.View.FindControl<Button>("ConfirmOkButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessUi.Pump(320);

        Assert.False(confirmation.IsVisible);
        Assert.True(ui.View.IsVisible);
        Assert.Equal(SettingsTab.About, ui.Tabs.SelectedTab);
        Assert.Equal("en", ui.ViewModel.Language);
        Assert.Equal("dark", ui.ViewModel.ThemeSetting);
        Assert.Equal(100, ui.ViewModel.UiScalePercent);
        Assert.True(ui.ViewModel.RoundedCorners);
        Assert.Equal("system", ui.ViewModel.MotionSetting);

        var restored = new SettingsStore(Home.Paths).Load();
        Assert.Equal("en", restored.Language);
        Assert.Equal("dark", restored.Theme);
        Assert.Equal(100, restored.UiScale);
        Assert.True(restored.RoundedCorners);
        Assert.Equal("system", restored.MotionPreference);
        Assert.True(restored.CheckForAppUpdates);
        Assert.Equal("beta", restored.SelectedToken);
        Assert.True(restored.SetupWizardCompleted);
    });

    private static void AssertPane(SettingsUiHost ui, SettingsTab selected)
    {
        Assert.Equal(selected, ui.Tabs.SelectedTab);

        foreach (var tab in SettingsTabs)
        {
            var isSelected = tab.Tab == selected;
            Assert.Equal(isSelected, ui.Pane(tab.Pane).IsVisible);
            Assert.Equal(isSelected, ui.TabButton(tab.Button).Classes.Contains("selected"));
        }
    }

    private static string? Label(SettingsUiHost ui, string button) =>
        (ui.TabButton(button).Content as TextBlock)?.Text;

    private static KeyEventArgs KeyEvent(Key key) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
    };
}
