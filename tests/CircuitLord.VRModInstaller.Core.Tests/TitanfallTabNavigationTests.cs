using CircuitLord.VRModInstaller.Core.Navigation;

namespace CircuitLord.VRModInstaller.Core.Tests;

public sealed class TitanfallTabNavigationTests
{
    [Fact]
    public void OverviewIsTheDefaultSessionDestination()
    {
        var session = new TabSession<TitanfallTab>();

        Assert.Equal(TitanfallTab.Overview, session.SelectedTab);
        Assert.False(session.HasSelection);
    }

    [Theory]
    [InlineData(TitanfallTab.Overview)]
    [InlineData(TitanfallTab.Releases)]
    [InlineData(TitanfallTab.History)]
    [InlineData(TitanfallTab.Diagnostics)]
    public void EveryDestinationCanBeSelected(TitanfallTab destination)
    {
        var session = new TabSession<TitanfallTab>();
        var changes = new List<TabSelectionChangedEventArgs<TitanfallTab>>();
        session.SelectedTabChanged += (_, args) => changes.Add(args);

        Assert.True(session.Select(destination));
        Assert.Equal(destination, session.SelectedTab);
        Assert.Single(changes);
        Assert.Equal(TitanfallTab.Overview, changes[0].Previous);
        Assert.Equal(destination, changes[0].Selected);
    }

    [Fact]
    public void SelectingTheCurrentDestinationDoesNotRaiseOrResetState()
    {
        var session = new TabSession<TitanfallTab>();
        session.Select(TitanfallTab.Releases);
        session.SetState(TitanfallTab.Releases, "filter=beta");
        var changes = 0;
        session.SelectedTabChanged += (_, _) => changes++;

        Assert.False(session.Select(TitanfallTab.Releases));
        Assert.Equal(0, changes);
        Assert.Equal("filter=beta", session.GetState<string>(TitanfallTab.Releases));
    }

    [Fact]
    public void ArrowMovementWrapsInBothDirections()
    {
        Assert.Equal(TitanfallTab.Releases, TabNavigation<TitanfallTab>.Move(TitanfallTab.Overview, 1));
        Assert.Equal(TitanfallTab.Diagnostics, TabNavigation<TitanfallTab>.Move(TitanfallTab.Overview, -1));
        Assert.Equal(TitanfallTab.Overview, TabNavigation<TitanfallTab>.Move(TitanfallTab.Diagnostics, 1));
        Assert.Equal(TitanfallTab.Overview, TabNavigation<TitanfallTab>.Move(TitanfallTab.Releases, -1));
        Assert.Equal(TitanfallTab.Overview, TabNavigation<TitanfallTab>.First);
        Assert.Equal(TitanfallTab.Diagnostics, TabNavigation<TitanfallTab>.Last);
    }

    [Fact]
    public void StateSurvivesVisitingAnotherDestination()
    {
        var session = new TabSession<TitanfallTab>();
        session.Select(TitanfallTab.History);
        session.SetState(TitanfallTab.History, new HistoryState("failed", "newest"));
        session.SetState(TitanfallTab.Overview, 480d);

        session.Select(TitanfallTab.Diagnostics);
        session.Select(TitanfallTab.History);

        var history = session.GetState<HistoryState>(TitanfallTab.History);
        Assert.NotNull(history);
        Assert.Equal("failed", history!.Filter);
        Assert.Equal("newest", history.Sort);
        Assert.Equal(480d, session.GetState<double>(TitanfallTab.Overview));
    }

    private sealed record HistoryState(string Filter, string Sort);
}
