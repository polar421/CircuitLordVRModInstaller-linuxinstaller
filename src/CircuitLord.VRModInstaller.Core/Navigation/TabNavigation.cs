using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitLord.VRModInstaller.Core.Navigation;

/// <summary>
/// Shared ordering, wrap-around and selection rules for a tab destination set.
/// The enum's declaration order is the tab order; both tab strips use this
/// instead of maintaining separate navigation copies.
/// </summary>
public static class TabNavigation<TTab> where TTab : struct, Enum
{
    public static IReadOnlyList<TTab> All { get; } = Array.AsReadOnly(Enum.GetValues<TTab>());

    public static bool IsValid(TTab tab) => All.Contains(tab);

    /// <summary>Moves by <paramref name="offset"/>, wrapping in both directions.</summary>
    public static TTab Move(TTab current, int offset)
    {
        var index = IndexOf(current);
        if (index < 0)
        {
            return First;
        }

        var count = All.Count;
        var next = ((index + offset) % count + count) % count;
        return All[next];
    }

    public static TTab First => All[0];

    public static TTab Last => All[^1];

    private static int IndexOf(TTab tab)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (EqualityComparer<TTab>.Default.Equals(All[index], tab))
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed class TabSelectionChangedEventArgs<TTab> : EventArgs where TTab : struct, Enum
{
    public TabSelectionChangedEventArgs(TTab previous, TTab selected)
    {
        Previous = previous;
        Selected = selected;
    }

    public TTab Previous { get; }

    public TTab Selected { get; }
}

/// <summary>
/// Small session object shared by both tab strips. The selected destination and
/// per-destination state survive a strip being hidden or rebuilt by a theme.
/// </summary>
public sealed class TabSession<TTab> where TTab : struct, Enum
{
    private readonly Dictionary<TTab, object?> _state = new();

    public TTab SelectedTab { get; private set; } = TabNavigation<TTab>.First;

    public bool HasSelection { get; private set; }

    public event EventHandler<TabSelectionChangedEventArgs<TTab>>? SelectedTabChanged;

    public bool Select(TTab tab)
    {
        if (!TabNavigation<TTab>.IsValid(tab) || (HasSelection && EqualityComparer<TTab>.Default.Equals(tab, SelectedTab)))
        {
            return false;
        }

        var previous = SelectedTab;
        SelectedTab = tab;
        HasSelection = true;
        SelectedTabChanged?.Invoke(this, new TabSelectionChangedEventArgs<TTab>(previous, tab));
        return true;
    }

    public void SetState<T>(TTab tab, T state) => _state[tab] = state;

    public T? GetState<T>(TTab tab)
        => _state.TryGetValue(tab, out var state) && state is T typed ? typed : default;
}
