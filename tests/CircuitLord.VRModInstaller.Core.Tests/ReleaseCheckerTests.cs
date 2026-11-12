using System;
using CircuitLord.VRModInstaller.Core.Updates;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class ReleaseCheckerTests
{
    private static readonly Version Current = new(1, 0, 0);

    [Theory]
    [InlineData("v1.0.0", false)]
    [InlineData("v1.0.0-beta.1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("v1.0.1", true)]
    [InlineData("v1.1.0-rc.1", true)]
    [InlineData("v2.0.0", true)]
    [InlineData("not-a-version", false)]
    public void ComparesTheNumericCoreOfReleaseTags(string tag, bool expected)
    {
        Assert.Equal(expected, ReleaseChecker.IsNewer(tag, Current));
    }
}
