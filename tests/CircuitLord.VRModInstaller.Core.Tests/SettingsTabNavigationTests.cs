using CircuitLord.VRModInstaller.Core.Navigation;
using CircuitLord.VRModInstaller.Core.Settings;

namespace CircuitLord.VRModInstaller.Core.Tests;

public sealed class SettingsTabNavigationTests
{
    [Fact]
    public void GeneralIsTheDefaultSessionDestination()
    {
        var session = new TabSession<SettingsTab>();

        Assert.Equal(SettingsTab.General, session.SelectedTab);
        Assert.False(session.HasSelection);
        Assert.Equal(4, TabNavigation<SettingsTab>.All.Count);
    }

    [Theory]
    [InlineData(SettingsTab.General)]
    [InlineData(SettingsTab.Appearance)]
    [InlineData(SettingsTab.Diagnostics)]
    [InlineData(SettingsTab.About)]
    public void EverySettingsDestinationCanBeSelected(SettingsTab destination)
    {
        var session = new TabSession<SettingsTab>();
        var changes = new List<TabSelectionChangedEventArgs<SettingsTab>>();
        session.SelectedTabChanged += (_, args) => changes.Add(args);

        Assert.True(session.Select(destination));
        Assert.Equal(destination, session.SelectedTab);
        Assert.Single(changes);
        Assert.Equal(SettingsTab.General, changes[0].Previous);
        Assert.Equal(destination, changes[0].Selected);
    }

    [Fact]
    public void SelectingTheCurrentDestinationDoesNotRaiseOrResetState()
    {
        var session = new TabSession<SettingsTab>();
        session.Select(SettingsTab.Appearance);
        session.SetState(SettingsTab.Appearance, 140d);
        var changes = 0;
        session.SelectedTabChanged += (_, _) => changes++;

        Assert.False(session.Select(SettingsTab.Appearance));
        Assert.Equal(0, changes);
        Assert.Equal(140d, session.GetState<double>(SettingsTab.Appearance));
    }

    [Fact]
    public void ArrowMovementWrapsInBothDirections()
    {
        Assert.Equal(SettingsTab.Appearance, TabNavigation<SettingsTab>.Move(SettingsTab.General, 1));
        Assert.Equal(SettingsTab.About, TabNavigation<SettingsTab>.Move(SettingsTab.General, -1));
        Assert.Equal(SettingsTab.General, TabNavigation<SettingsTab>.Move(SettingsTab.About, 1));
        Assert.Equal(SettingsTab.General, TabNavigation<SettingsTab>.Move(SettingsTab.Appearance, -1));
        Assert.Equal(SettingsTab.General, TabNavigation<SettingsTab>.First);
        Assert.Equal(SettingsTab.About, TabNavigation<SettingsTab>.Last);
    }

    [Fact]
    public void EachTabKeepsAnIndependentScrollPosition()
    {
        var session = new TabSession<SettingsTab>();
        session.SetState(SettingsTab.General, 0d);
        session.SetState(SettingsTab.Appearance, 128d);
        session.SetState(SettingsTab.Diagnostics, 456d);
        session.SetState(SettingsTab.About, 72d);

        session.Select(SettingsTab.Diagnostics);
        session.Select(SettingsTab.About);
        session.Select(SettingsTab.Appearance);

        Assert.Equal(0d, session.GetState<double>(SettingsTab.General));
        Assert.Equal(128d, session.GetState<double>(SettingsTab.Appearance));
        Assert.Equal(456d, session.GetState<double>(SettingsTab.Diagnostics));
        Assert.Equal(72d, session.GetState<double>(SettingsTab.About));
    }
}
