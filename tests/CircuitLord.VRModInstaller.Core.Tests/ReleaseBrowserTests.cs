using System;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Catalogue;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Releases;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class ReleaseBrowserTests
{
    private const string CatalogueFixture = """
        Titanfall 2 VR versions:
          Follow updates:
            1) latest      1.0.9
            2) beta        1.0.10  beta build
            3) alpha       1.0.11  experimental channel
          Stay on one version:
            4) 1.0.8       1.0.8   older stable build
            5) beta-1.0.7  1.0.7   legacy pinned build
        """;

    [Fact]
    public void ChannelKeysKeepPinnedReleasesOutOfMovingChannels()
    {
        var items = BuildFixture();

        Assert.Equal("stable", items.Single(item => item.Token == "latest").ChannelKey);
        Assert.Equal("beta", items.Single(item => item.Token == "beta").ChannelKey);
        Assert.Equal("alpha", items.Single(item => item.Token == "alpha").ChannelKey);
        Assert.Equal("pinned", items.Single(item => item.Token == "1.0.8").ChannelKey);
        Assert.Equal("pinned", items.Single(item => item.Token == "beta-1.0.7").ChannelKey);
    }

    [Fact]
    public void SearchMatchesVersionChannelTokenDescriptionAndChangelog()
    {
        var items = BuildFixture();

        Assert.Equal("alpha", Single(ReleaseBrowser.Filter(items, ReleaseFilter.All, "1.0.11")).Token);
        Assert.Equal("alpha", Single(ReleaseBrowser.Filter(items, ReleaseFilter.All, "experimental")).Token);
        Assert.Equal("beta-1.0.7", Single(ReleaseBrowser.Filter(items, ReleaseFilter.All, "beta-1.0.7")).Token);
        Assert.Equal("beta", Single(ReleaseBrowser.Filter(items, ReleaseFilter.All, "beta build")).Token);
        Assert.Equal("1.0.8", Single(ReleaseBrowser.Filter(items, ReleaseFilter.All, "pinned notes")).Token);
    }

    [Fact]
    public void FiltersUseCatalogueAndSelectionState()
    {
        var items = BuildFixture();

        Assert.Equal(["latest"], ReleaseBrowser.Filter(items, ReleaseFilter.Stable, null).Select(item => item.Token));
        Assert.Equal(["beta"], ReleaseBrowser.Filter(items, ReleaseFilter.Beta, null).Select(item => item.Token));
        Assert.Equal(["1.0.8", "beta-1.0.7"], ReleaseBrowser.Filter(items, ReleaseFilter.Pinned, null).Select(item => item.Token));
        Assert.Equal(["beta"], ReleaseBrowser.Filter(items, ReleaseFilter.Installed, null).Select(item => item.Token));
        Assert.Equal(["1.0.8"], ReleaseBrowser.Filter(items, ReleaseFilter.Selected, null).Select(item => item.Token));
    }

    [Fact]
    public void SortingUsesSemanticVersionsAndKeepsCatalogueOrderAvailable()
    {
        var items = BuildFixture();

        Assert.Equal(["alpha", "beta", "latest", "1.0.8", "beta-1.0.7"],
            ReleaseBrowser.Sort(items, ReleaseSort.NewestFirst).Select(item => item.Token));
        Assert.Equal(["beta-1.0.7", "1.0.8", "latest", "beta", "alpha"],
            ReleaseBrowser.Sort(items, ReleaseSort.OldestFirst).Select(item => item.Token));
        Assert.Equal(["latest", "beta", "alpha", "1.0.8", "beta-1.0.7"],
            ReleaseBrowser.Sort(items, ReleaseSort.CatalogueOrder).Select(item => item.Token));
        Assert.Equal("latest", ReleaseBrowser.Sort(items, ReleaseSort.StableFirst).First().Token);
        Assert.Equal("beta", ReleaseBrowser.Sort(items, ReleaseSort.BetaFirst).First().Token);
    }

    [Fact]
    public void CachedEntriesRemainMarkedAndRemovedEntriesAreAbsent()
    {
        var catalogue = ParseFixture();
        var cached = new VersionCatalogue
        {
            Entries = catalogue.Entries.Select(entry => new VersionCatalogueEntry
            {
                Ordinal = entry.Ordinal,
                BackendToken = entry.BackendToken,
                DisplayName = entry.DisplayName,
                ResolvedVersion = entry.ResolvedVersion,
                ChannelId = entry.ChannelId,
                Description = entry.Description,
                IsMovingChannel = entry.IsMovingChannel,
                IsPinnedRelease = entry.IsPinnedRelease,
                CatalogueSource = CatalogueSource.Cached,
            }).ToList(),
            RetrievedAt = catalogue.RetrievedAt,
            IsFromCache = true,
        };

        var items = ReleaseBrowser.Build(cached, null, null);
        Assert.All(items, item => Assert.True(item.IsCached));

        var removed = new VersionCatalogue
        {
            Entries = catalogue.Entries.Where(entry => entry.BackendToken != "alpha").ToList(),
            RetrievedAt = catalogue.RetrievedAt,
        };
        Assert.DoesNotContain(
            ReleaseBrowser.Build(removed, null, null),
            item => item.Token == "alpha");
    }

    private static ReleaseBrowserItem Single(System.Collections.Generic.IReadOnlyList<ReleaseBrowserItem> items)
    {
        Assert.Single(items);
        return items[0];
    }

    private static System.Collections.Generic.IReadOnlyList<ReleaseBrowserItem> BuildFixture()
    {
        var catalogue = ParseFixture();
        return ReleaseBrowser.Build(
            catalogue,
            installedToken: "beta",
            selectedToken: "1.0.8",
            changelog: entry => entry.BackendToken == "1.0.8" ? "pinned notes" : null);
    }

    private static VersionCatalogue ParseFixture()
        => CatalogueParser.Parse(CatalogueFixture, "beta", DateTimeOffset.UnixEpoch);
}
