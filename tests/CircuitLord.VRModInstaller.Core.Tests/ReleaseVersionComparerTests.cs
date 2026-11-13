using CircuitLord.VRModInstaller.Core.Releases;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class ReleaseVersionComparerTests
{
    [Fact]
    public void NumericComponentsSortNaturally()
    {
        Assert.True(ReleaseVersionComparer.Compare("1.0.10", "1.0.9") > 0);
        Assert.True(ReleaseVersionComparer.Compare("1.10.0", "1.9.99") > 0);
        Assert.True(ReleaseVersionComparer.Compare("v2.0.0", "2.0.0") == 0);
    }

    [Fact]
    public void PrereleasesSortBeforeTheMatchingStableVersion()
    {
        Assert.True(ReleaseVersionComparer.Compare("1.0.10", "1.0.10-beta.2") > 0);
        Assert.True(ReleaseVersionComparer.Compare("1.0.10-beta.10", "1.0.10-beta.2") > 0);
        Assert.True(ReleaseVersionComparer.Compare("1.0.10-beta.2", "1.0.10-alpha.1") > 0);
    }
}
