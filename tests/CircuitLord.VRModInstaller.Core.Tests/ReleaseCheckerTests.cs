using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public async Task ReleaseFeedExposesTheLinuxArchiveAndChecksum()
    {
        const string json = """
            [
              {
                "tag_name": "v1.0.2",
                "html_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/tag/v1.0.2",
                "draft": false,
                "assets": [
                  {
                    "name": "circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz",
                    "browser_download_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/download/v1.0.2/circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz",
                    "size": 12345
                  },
                  {
                    "name": "circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz.sha256",
                    "browser_download_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/download/v1.0.2/circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz.sha256",
                    "size": 116
                  }
                ]
              }
            ]
            """;

        using var http = new HttpClient(new StubHandler(json));
        var result = await new ReleaseChecker(http).CheckAsync(new Version(1, 0, 1), CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.Equal("v1.0.2", result.LatestTag);
        Assert.True(result.CanAutoUpdate);
        Assert.Equal("circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz", result.Archive!.Name);
        Assert.Equal("circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz.sha256", result.Checksum!.Name);
    }

    [Fact]
    public async Task ReleaseWithoutChecksumCannotAutoUpdate()
    {
        const string json = """
            [
              {
                "tag_name": "v1.0.2",
                "html_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/tag/v1.0.2",
                "draft": false,
                "assets": [
                  {
                    "name": "circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz",
                    "browser_download_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/download/v1.0.2/circuitlord-vrmodinstaller-1.0.2-linux-x64.tar.gz",
                    "size": 12345
                  }
                ]
              }
            ]
            """;

        using var http = new HttpClient(new StubHandler(json));
        var result = await new ReleaseChecker(http).CheckAsync(new Version(1, 0, 1), CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.NotNull(result.Archive);
        Assert.Null(result.Checksum);
        Assert.False(result.CanAutoUpdate);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }
}
