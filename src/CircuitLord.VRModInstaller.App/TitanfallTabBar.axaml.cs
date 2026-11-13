using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Core.Navigation;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Reusable four-destination tab strip for the Titanfall 2 VR page. Shared
/// selection, keyboard and automation behavior lives in TabBarController.
/// </summary>
public partial class TitanfallTabBar : UserControl
{
    private readonly TabBarController<TitanfallTab> _tabs;

    public TitanfallTabBar()
    {
        InitializeComponent();
        _tabs = new TabBarController<TitanfallTab>(this, "TabButtons", LabelFor);
        _tabs.SelectionChanged += (_, args) => SelectionChanged?.Invoke(this, args);
    }

    public event EventHandler<TabSelectionChangedEventArgs<TitanfallTab>>? SelectionChanged;

    public TitanfallTab SelectedTab => _tabs.SelectedTab;

    public void SelectTab(TitanfallTab tab, bool focus, bool raiseEvent = true)
        => _tabs.Select(tab, focus, raiseEvent);

    public void FocusSelectedButton() => _tabs.FocusSelectedButton();

    public void RefreshAppearance() => _tabs.RefreshAppearance();

    /// <summary>Shows the background-operation indicator without inventing progress.</summary>
    public void SetActivity(bool active, string? text)
    {
        var indicator = this.FindControl<Border>("ActivityIndicator");
        var label = this.FindControl<TextBlock>("ActivityText");
        if (indicator is null || label is null)
        {
            return;
        }

        var message = text ?? string.Empty;
        indicator.IsVisible = active && message.Length > 0;
        label.Text = message;
        AutomationProperties.SetName(indicator, message);
    }

    private static string LabelFor(TitanfallTab tab) => Loc.Get(tab switch
    {
        TitanfallTab.Overview => "tabOverview",
        TitanfallTab.Releases => "tabReleases",
        TitanfallTab.History => "tabHistory",
        _ => "tabDiagnostics",
    });

    private void Tab_Click(object? sender, RoutedEventArgs e) => _tabs.HandleClick(sender);

    private void TabButton_KeyDown(object? sender, KeyEventArgs e)
        => e.Handled = _tabs.HandleKey(e.Key);
}
