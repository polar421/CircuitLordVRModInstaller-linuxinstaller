using System;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Catalogue;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class CatalogueParserTests
{
    private const string RealWorldOutput =
        "Titanfall 2 VR versions:\n" +
        "  Follow updates:\n" +
        "    1) latest     1.0.25  2026-10-24  [installed]  <- your choice\n" +
        "    2) beta       1.0.27  New features, may be unstable\n" +
        "  Stay on one version:\n" +
        "    3) 1.0.24     1.0.24  2026-10-23\n" +
        "    4) beta-1.0.22  1.0.22  beta build\n" +
        "\n" +
        "Install one with:  ./install.sh --version NAME   (or just ./install.sh to pick from this list)\n";

    [Fact]
    public void StableIsFirstAndBetaSecondWithExactTokens()
    {
        var catalogue = CatalogueParser.Parse(RealWorldOutput, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(4, catalogue.Entries.Count);
        Assert.Equal("latest", catalogue.Entries[0].BackendToken);
        Assert.Equal("1 — Stable / Latest", catalogue.Entries[0].DisplayLabel);
        Assert.Equal(1, catalogue.Entries[0].Ordinal);
        Assert.Equal("beta", catalogue.Entries[1].BackendToken);
        Assert.Equal(2, catalogue.Entries[1].Ordinal);
        Assert.Equal("1.0.24", catalogue.Entries[2].BackendToken);
        Assert.Equal("beta-1.0.22", catalogue.Entries[3].BackendToken);
    }

    [Fact]
    public void OrdinalsArePresentationOnlyAndNeverBecomeTheRequestedArgument()
    {
        var catalogue = CatalogueParser.Parse(RealWorldOutput, null, DateTimeOffset.UnixEpoch);

        // Entry one is "latest", not "1"; entry three is "1.0.24", not "3".
        Assert.Equal("latest", catalogue.Entries.Single(e => e.Ordinal == 1).BackendToken);
        Assert.Equal("1.0.24", catalogue.Entries.Single(e => e.Ordinal == 3).BackendToken);
        Assert.DoesNotContain(catalogue.Entries, e => e.BackendToken == "3");
    }

    [Fact]
    public void InstalledMarkerAndBackendChoiceAreRead()
    {
        var catalogue = CatalogueParser.Parse(RealWorldOutput, null, DateTimeOffset.UnixEpoch);

        Assert.True(catalogue.Entries[0].IsInstalled);
        Assert.True(catalogue.Entries[0].IsSelectedByBackend);
        Assert.False(catalogue.Entries[1].IsInstalled);
    }

    [Fact]
    public void ReorderingTheCatalogueDoesNotChangeATokenSelection()
    {
        var reordered = RealWorldOutput
            .Replace("    3) 1.0.24     1.0.24  2026-10-23", "    3) 0.9.9      0.9.9  2026-01-01")
            .Replace("    4) beta-1.0.22  1.0.22  beta build", "    4) 1.0.24     1.0.24  2026-10-23");

        var original = CatalogueParser.Parse(RealWorldOutput, null, DateTimeOffset.UnixEpoch);
        var changed = CatalogueParser.Parse(reordered, null, DateTimeOffset.UnixEpoch);

        Assert.Equal("1.0.24", original.Entries.Single(e => e.Ordinal == 3).BackendToken);
        Assert.Equal("1.0.24", changed.Entries.Single(e => e.BackendToken == "1.0.24").BackendToken);
        Assert.NotEqual(
            original.Entries.Single(e => e.BackendToken == "1.0.24").Ordinal,
            changed.Entries.Single(e => e.BackendToken == "1.0.24").Ordinal);
    }

    [Fact]
    public void HandlesCrLfAndAnsiSequences()
    {
        var noisy = "\u001b[32mTitanfall 2 VR versions:\u001b[0m\r\n" +
                    "  Follow updates:\r\n" +
                    "    1) latest     1.0.25  latest\r\n";

        var catalogue = CatalogueParser.Parse(noisy, null, DateTimeOffset.UnixEpoch);

        Assert.Single(catalogue.Entries);
        Assert.Equal("latest", catalogue.Entries[0].BackendToken);
    }

    [Fact]
    public void EmptyOrUnrecognisedOutputProducesNoGuessedTokens()
    {
        var empty = CatalogueParser.Parse(string.Empty, null, DateTimeOffset.UnixEpoch);
        var garbage = CatalogueParser.Parse("boom: something went wrong", null, DateTimeOffset.UnixEpoch);

        Assert.Empty(empty.Entries);
        Assert.NotNull(empty.Diagnostic);
        Assert.Empty(garbage.Entries);
        Assert.NotNull(garbage.Diagnostic);
    }

    [Theory]
    [InlineData("latest", true)]
    [InlineData("beta", true)]
    [InlineData("1.0.24", true)]
    [InlineData("beta-1.0.22", true)]
    [InlineData("--force", false)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    public void TokenValidationRejectsOptionInjection(string token, bool expected)
    {
        Assert.Equal(expected, CatalogueParser.IsValidToken(token));
    }

    [Fact]
    public void NumericTokenIsKeptVerbatimInsteadOfBeingTreatedAsAnOrdinal()
    {
        var output = "Titanfall 2 VR versions:\n  Stay on one version:\n    1) 1.0.24  1.0.24  2026-10-23\n";
        var catalogue = CatalogueParser.Parse(output, null, DateTimeOffset.UnixEpoch);

        Assert.Equal("1.0.24", catalogue.Entries[0].BackendToken);
        Assert.Equal(1, catalogue.Entries[0].Ordinal);
    }
}
