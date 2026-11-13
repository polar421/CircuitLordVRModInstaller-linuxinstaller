using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using CircuitLord.VRModInstaller.Core.Navigation;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Shared presentation behavior for a strip of buttons tagged with an enum
/// destination. The owning controls supply the button panel and localized label
/// lookup; selecting, keyboard movement and automation metadata stay in one place.
/// </summary>
internal sealed class TabBarController<TTab> where TTab : struct, Enum
{
    private readonly UserControl _owner;
    private readonly string _buttonPanelName;
    private readonly Func<TTab, string> _labelFor;
    private readonly TabSession<TTab> _session = new();

    public TabBarController(UserControl owner, string buttonPanelName, Func<TTab, string> labelFor)
    {
        _owner = owner;
        _buttonPanelName = buttonPanelName;
        _labelFor = labelFor;
        owner.AttachedToVisualTree += (_, _) => Loc.LanguageChanged += LanguageChanged;
        owner.DetachedFromVisualTree += (_, _) => Loc.LanguageChanged -= LanguageChanged;
        Select(TabNavigation<TTab>.First, focus: false, raiseEvent: false);
    }

    public event EventHandler<TabSelectionChangedEventArgs<TTab>>? SelectionChanged;

    public TTab SelectedTab => _session.SelectedTab;

    public void Select(TTab tab, bool focus, bool raiseEvent = true)
    {
        if (!TabNavigation<TTab>.IsValid(tab))
        {
            return;
        }

        var previous = SelectedTab;
        var changed = _session.Select(tab);
        UpdateSelectedState();

        if (focus)
        {
            var button = ButtonFor(tab);
            button?.Focus();
            button?.BringIntoView();
        }

        if (changed && raiseEvent)
        {
            SelectionChanged?.Invoke(this, new TabSelectionChangedEventArgs<TTab>(previous, tab));
        }
    }

    public void FocusSelectedButton()
    {
        var button = ButtonFor(SelectedTab);
        button?.Focus();
        button?.BringIntoView();
    }

    public void RefreshAppearance() => UpdateSelectedState();

    public void HandleClick(object? sender)
    {
        if (sender is not Button { Tag: string value }
            || !Enum.TryParse<TTab>(value, ignoreCase: true, out var tab))
        {
            return;
        }

        Select(tab, focus: false);
    }

    public bool HandleKey(Key key)
    {
        var destination = key switch
        {
            Key.Left => TabNavigation<TTab>.Move(SelectedTab, -1),
            Key.Right => TabNavigation<TTab>.Move(SelectedTab, 1),
            Key.Home => TabNavigation<TTab>.First,
            Key.End => TabNavigation<TTab>.Last,
            _ => (TTab?)null,
        };

        if (destination is not { } tab)
        {
            return false;
        }

        Select(tab, focus: true);
        return true;
    }

    private IEnumerable<Button> TabButtons()
        => _owner.FindControl<StackPanel>(_buttonPanelName)?.Children.OfType<Button>()
           ?? Enumerable.Empty<Button>();

    private Button? ButtonFor(TTab tab)
        => TabButtons().FirstOrDefault(button =>
            button.Tag is string value
            && Enum.TryParse<TTab>(value, ignoreCase: true, out var parsed)
            && EqualityComparer<TTab>.Default.Equals(parsed, tab));

    private void UpdateSelectedState()
    {
        foreach (var button in TabButtons())
        {
            if (button.Tag is not string value
                || !Enum.TryParse<TTab>(value, ignoreCase: true, out var tab))
            {
                continue;
            }

            var selected = EqualityComparer<TTab>.Default.Equals(tab, SelectedTab);
            if (selected)
            {
                if (!button.Classes.Contains("selected"))
                {
                    button.Classes.Add("selected");
                }
            }
            else
            {
                button.Classes.Remove("selected");
            }

            var label = _labelFor(tab);
            AutomationProperties.SetName(
                button,
                selected ? label + " · " + Loc.Get("tabSelected") : label);
            AutomationProperties.SetHelpText(button, Loc.Get("tabSwitchHint"));
            AutomationProperties.SetItemStatus(
                button,
                selected ? Loc.Get("tabSelected") : Loc.Get("tabNotSelected"));
            AutomationProperties.SetControlTypeOverride(button, AutomationControlType.TabItem);
            AutomationProperties.SetPositionInSet(button, Convert.ToInt32(tab) + 1);
            AutomationProperties.SetSizeOfSet(button, TabNavigation<TTab>.All.Count);
        }
    }

    private void LanguageChanged(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSelectedState);
}
