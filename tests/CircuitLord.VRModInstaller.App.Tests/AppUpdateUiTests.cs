using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitLord.VRModInstaller.Core;
using CircuitLord.VRModInstaller.Core.Updates;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class AppUpdateUiTests : HeadlessUiTest
{
    private const string PackageName = "circuitlord-vrmodinstaller-99.0.0-beta.1-linux-x64";
    private const string ArchiveName = PackageName + ".tar.gz";
    private const string FeedWithAssets = """
        [
          {
            "tag_name": "v99.0.0-beta.1",
            "html_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/tag/v99.0.0-beta.1",
            "draft": false,
            "prerelease": true,
            "assets": [
              {
                "name": "circuitlord-vrmodinstaller-99.0.0-beta.1-linux-x64.tar.gz",
                "browser_download_url": "https://example.test/update.tar.gz",
                "size": 13
              },
              {
                "name": "circuitlord-vrmodinstaller-99.0.0-beta.1-linux-x64.tar.gz.sha256",
                "browser_download_url": "https://example.test/update.tar.gz.sha256",
                "size": 116
              }
            ]
          }
        ]
        """;
    private const string FeedWithoutAssets = """
        [
          {
            "tag_name": "v99.0.0-beta.1",
            "html_url": "https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/tag/v99.0.0-beta.1",
            "draft": false,
            "prerelease": true,
            "assets": []
          }
        ]
        """;

    [Fact]
    public Task BetaCorruptedChecksumShowsFailureInSettings() => Dispatch(() =>
    {
        using var updaterHttp = new HttpClient(new UpdateAssetHandler(
            Encoding.UTF8.GetBytes("not a tarball"),
            new string('a', 64) + "  " + ArchiveName + "\n"));
        using var feedHttp = new HttpClient(new FeedHandler(FeedWithAssets));
        var installed = CreateInstalledApp(Home);
        var updater = new AppUpdateService(
            Home.Paths,
            http: updaterHttp,
            currentProcessPath: () => installed);

        using var ui = new SettingsUiHost(
            Home,
            settings =>
            {
                settings.CheckForAppUpdates = false;
                settings.UpdateChannel = ReleaseChannelPreference.Beta;
            },
            createViewModel: () => new InstallerViewModel(updater, new ReleaseChecker(feedHttp)));

        WaitFor(ui.ViewModel.CheckForUpdatesAsync());
        WaitFor(ui.ViewModel.InstallAppUpdateAsync());
        HeadlessUi.Pump(120);

        var text = ui.View.FindControl<TextBlock>("SettingsUpdateStatus")!.Text ?? string.Empty;
        Assert.True(ui.ViewModel.UpdateAvailable);
        Assert.Contains(Loc.Get("appUpdateFailed").Split('{')[0], text, StringComparison.Ordinal);
        Assert.Contains("checksum", text, StringComparison.OrdinalIgnoreCase);
        AssertInstalledAppRuns(installed);
    });

    [Fact]
    public Task BetaMissingUpdateAssetsShowsFailureInSettings() => Dispatch(() =>
    {
        using var feedHttp = new HttpClient(new FeedHandler(FeedWithoutAssets));
        var installed = CreateInstalledApp(Home);
        var updater = new AppUpdateService(Home.Paths, currentProcessPath: () => installed);

        using var ui = new SettingsUiHost(
            Home,
            settings =>
            {
                settings.CheckForAppUpdates = false;
                settings.UpdateChannel = ReleaseChannelPreference.Beta;
            },
            createViewModel: () => new InstallerViewModel(updater, new ReleaseChecker(feedHttp)));

        WaitFor(ui.ViewModel.CheckForUpdatesAsync());
        WaitFor(ui.ViewModel.InstallAppUpdateAsync());
        HeadlessUi.Pump(120);

        var text = ui.View.FindControl<TextBlock>("SettingsUpdateStatus")!.Text ?? string.Empty;
        Assert.True(ui.ViewModel.UpdateAvailable);
        Assert.Contains(Loc.Get("appUpdatePackageMissing"), text, StringComparison.Ordinal);
        AssertInstalledAppRuns(installed);
    });

    private static void AssertInstalledAppRuns(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrEmpty(error), error);
        Assert.Equal("old-app-ok", output.Trim());
    }

    private static void WaitFor(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The app update operation did not complete.");
            }
            HeadlessUi.Pump(20);
        }
        task.GetAwaiter().GetResult();
    }

    private static string CreateInstalledApp(TemporaryHome home)
    {
        var installed = AppUpdateService.InstalledExecutablePath(home.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "#!/usr/bin/env bash\nprintf 'old-app-ok\\n'\n");
        File.SetUnixFileMode(
            installed,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return installed;
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        private readonly string _json;

        public FeedHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class UpdateAssetHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly string _checksum;

        public UpdateAssetHandler(byte[] archive, string checksum)
        {
            _archive = archive;
            _checksum = checksum;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isChecksum = request.RequestUri?.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) == true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isChecksum
                    ? new StringContent(_checksum, Encoding.UTF8, "text/plain")
                    : new ByteArrayContent(_archive),
            });
        }
    }
}
