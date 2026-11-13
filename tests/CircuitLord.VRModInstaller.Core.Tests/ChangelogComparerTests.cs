using CircuitLord.VRModInstaller.Core.Manifest;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class ChangelogComparerTests
{
    [Fact]
    public void RealChangelogsReportOnlyTheirUniqueLinesInEachDirection()
    {
        const string left = """
            ## 1.0.25

            - Improve frame pacing in crowded scenes.
            - Fix install failures when the game path contains spaces.
            - Add Japanese and Simplified Chinese translations.
            - Fix install failures when the game path contains spaces.
            """;
        const string right = """
            ## 1.0.27

            - Improve frame pacing in crowded scenes.
            - Add a release explorer with side-by-side comparison.
            - Fix a crash when Steam is not running.
            """;

        var comparison = ChangelogComparer.Compare(left, right);

        Assert.True(comparison.IsAvailable);
        Assert.False(comparison.IsIdentical);
        Assert.Equal(
            new[]
            {
                "## 1.0.25",
                "- Fix install failures when the game path contains spaces.",
                "- Add Japanese and Simplified Chinese translations.",
            },
            comparison.OnlyInLeft);
        Assert.Equal(
            new[]
            {
                "## 1.0.27",
                "- Add a release explorer with side-by-side comparison.",
                "- Fix a crash when Steam is not running.",
            },
            comparison.OnlyInRight);
    }

    [Fact]
    public void PlaceholderChangelogIsUnavailableInsteadOfAChangedLine()
    {
        const string placeholder = "A changelog will appear when release notes are available.";

        var comparison = ChangelogComparer.Compare(
            placeholder,
            "Beta notes",
            unavailablePlaceholder: placeholder);

        Assert.False(comparison.IsAvailable);
        Assert.False(comparison.IsIdentical);
        Assert.Empty(comparison.OnlyInLeft);
        Assert.Empty(comparison.OnlyInRight);
    }
}
