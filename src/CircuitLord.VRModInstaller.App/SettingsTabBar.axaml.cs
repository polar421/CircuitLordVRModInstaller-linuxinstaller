using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Core.Navigation;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Reusable four-destination tab strip for the Settings dialog. Shared
/// selection, keyboard and automation behavior lives in TabBarController.
/// </summary>
public partial class SettingsTabBar : UserControl
{
    private readonly TabBarController<SettingsTab> _tabs;

    public SettingsTabBar()
    {
        InitializeComponent();
        _tabs = new TabBarController<SettingsTab>(this, "SettingsTabButtons", LabelFor);
        _tabs.SelectionChanged += (_, args) => SelectionChanged?.Invoke(this, args);
    }

    public event EventHandler<TabSelectionChangedEventArgs<SettingsTab>>? SelectionChanged;

    public SettingsTab SelectedTab => _tabs.SelectedTab;

    public void SelectTab(SettingsTab tab, bool focus, bool raiseEvent = true)
        => _tabs.Select(tab, focus, raiseEvent);

    private static string LabelFor(SettingsTab tab) => Loc.Get(tab switch
    {
        SettingsTab.General => "general",
        SettingsTab.Appearance => "appearance",
        SettingsTab.Diagnostics => "diagnostics",
        _ => "about",
    });

    private void Tab_Click(object? sender, RoutedEventArgs e) => _tabs.HandleClick(sender);

    private void TabButton_KeyDown(object? sender, KeyEventArgs e)
        => e.Handled = _tabs.HandleKey(e.Key);
}
