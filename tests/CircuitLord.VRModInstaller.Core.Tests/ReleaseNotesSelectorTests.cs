using CircuitLord.VRModInstaller.Core.Manifest;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class ReleaseNotesSelectorTests
{
    [Fact]
    public void MatchingSelectedReleaseNotesWin()
    {
        var product = Product(
            Change("1.0.25", "Stable notes"),
            Change("1.0.27", "Beta notes"));

        var selection = ReleaseNotesSelector.Select(product, "1.0.27", "1.0.27");

        Assert.Equal(ReleaseNotesSource.SelectedRelease, selection.Source);
        Assert.Equal("1.0.27", selection.Version);
        Assert.Single(selection.Changes);
        Assert.Contains("Beta notes", selection.Changes[0].Notes);
    }

    [Fact]
    public void StableOnlyNotesAreNotTreatedAsSelectedBetaNotes()
    {
        var product = Product(Change("1.0.25", "Stable notes"));

        var selection = ReleaseNotesSelector.Select(product, "1.0.27", "1.0.27");

        Assert.Equal(ReleaseNotesSource.StableManifest, selection.Source);
        Assert.Equal("1.0.25", selection.Version);
        Assert.Single(selection.Changes);
        Assert.Contains("Stable notes", selection.Changes[0].Notes);
    }

    [Fact]
    public void InstalledReleaseNotesAreUsedWhenTheSelectionHasNoNotes()
    {
        var product = Product(Change("1.0.27", "Installed beta notes"));

        var selection = ReleaseNotesSelector.Select(product, "1.0.28", "1.0.27");

        Assert.Equal(ReleaseNotesSource.InstalledRelease, selection.Source);
        Assert.Equal("1.0.27", selection.Version);
        Assert.Contains("Installed beta notes", selection.Changes[0].Notes);
    }

    [Fact]
    public void MissingNotesStayMissing()
    {
        var selection = ReleaseNotesSelector.Select(Product(), "1.0.27", "1.0.27");

        Assert.Equal(ReleaseNotesSource.None, selection.Source);
        Assert.False(selection.HasNotes);
    }

    [Fact]
    public void ExactReleaseLookupDoesNotFallBackToAnotherVersion()
    {
        var product = Product(Change("1.0.25", "Stable notes"));

        var stable = ReleaseNotesSelector.FindForVersion(product, "v1.0.25");
        var beta = ReleaseNotesSelector.FindForVersion(product, "1.0.27");

        Assert.NotNull(stable);
        Assert.Contains("Stable notes", stable.Notes);
        Assert.Null(beta);
    }

    private static TitanfallManifestProduct Product(params ManifestChange[] changes) => new()
    {
        Id = "Titanfall2VR",
        Name = "Titanfall 2 VR",
        Version = "1.0.25",
        Changes = changes,
    };

    private static ManifestChange Change(string version, string notes) => new()
    {
        Version = version,
        Notes = notes,
    };
}
