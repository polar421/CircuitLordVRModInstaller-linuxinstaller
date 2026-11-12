using CircuitLord.VRModInstaller.Core.Updates;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class ReleaseChannelTests
{
    [Theory]
    [InlineData("auto", false, false)]
    [InlineData("auto", true, true)]
    [InlineData("stable", true, false)]
    [InlineData("beta", false, true)]
    [InlineData("garbage", false, false)]
    [InlineData("", true, true)]
    [InlineData(null, false, false)]
    public void AutomaticFollowsTheBuildAndExplicitPreferencesWin(
        string? preference,
        bool buildIsBeta,
        bool expectPrereleases)
    {
        Assert.Equal(expectPrereleases,
            ReleaseChannelPreference.IncludesPrereleases(preference, buildIsBeta));
    }

    [Theory]
    [InlineData("BETA", "beta")]
    [InlineData(" stable ", "stable")]
    [InlineData("nightly", "auto")]
    [InlineData(null, "auto")]
    public void PreferencesAreNormalized(string? input, string expected)
    {
        Assert.Equal(expected, ReleaseChannelPreference.Normalize(input));
    }
}
