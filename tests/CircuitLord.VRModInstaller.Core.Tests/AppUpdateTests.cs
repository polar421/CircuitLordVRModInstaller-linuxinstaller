using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core;
using CircuitLord.VRModInstaller.Core.Updates;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class AppUpdateTests
{
    [Fact]
    public void ChecksumFileMustContainASha256Digest()
    {
        const string digest = "12f6575c090c3915ca9fdd56c772b920918b5f6e36b25058d9453ff3bdacb925";
        Assert.Equal(digest, AppUpdateService.ParseChecksum(digest + "  circuitlord-vrmodinstaller.tar.gz\n"));
        Assert.Throws<InvalidDataException>(() => AppUpdateService.ParseChecksum("not-a-digest\n"));
    }

    [Fact]
    public void InstalledCopyIsRecognizedByItsRealOrSymlinkedPath()
    {
        using var env = new TempEnv();
        var installed = AppUpdateService.InstalledExecutablePath(env.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "binary");

        var link = Path.Combine(env.Root, "circuitlord-vrmodinstaller");
        File.CreateSymbolicLink(link, installed);

        Assert.True(AppUpdateService.IsInstalledCopy(env.Paths, installed));
        Assert.True(AppUpdateService.IsInstalledCopy(env.Paths, link));
        Assert.False(AppUpdateService.IsInstalledCopy(env.Paths, "/tmp/some-build/CircuitLordVRModInstaller"));
    }

    [Fact]
    public async Task PreparesAVerifiedReleasePackage()
    {
        using var env = new TempEnv();
        var installed = CreateInstalledApp(env);

        const string packageName = "circuitlord-vrmodinstaller-1.0.2-linux-x64";
        var packageRoot = Path.Combine(env.Root, "fixture", packageName);
        var appDirectory = Path.Combine(packageRoot, "app");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "CircuitLordVRModInstaller"), "new binary");
        File.WriteAllText(Path.Combine(packageRoot, "install.sh"), "#!/usr/bin/env bash\nexit 0\n");
        File.SetUnixFileMode(
            Path.Combine(packageRoot, "install.sh"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "SHA256SUMS")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Sha256(path) + "  " + Path.GetRelativePath(packageRoot, path))
            .ToArray();
        File.WriteAllLines(Path.Combine(packageRoot, "SHA256SUMS"), files);

        var tarPath = Path.Combine(env.Root, "fixture", packageName + ".tar");
        var archive = Path.Combine(env.Root, "fixture", packageName + ".tar.gz");
        TarFile.CreateFromDirectory(packageRoot, tarPath, includeBaseDirectory: true);
        using (var input = File.OpenRead(tarPath))
        using (var output = File.Create(archive))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            input.CopyTo(gzip);
        }
        File.Delete(tarPath);
        var archiveBytes = File.ReadAllBytes(archive);
        var checksum = Sha256(archive);

        using var http = new HttpClient(new AssetHandler(archiveBytes, checksum + "  " + packageName + ".tar.gz\n"));
        var service = new AppUpdateService(
            env.Paths,
            http: http,
            currentProcessPath: () => installed);
        var release = Release(
            "v1.0.2",
            new ReleaseAsset(packageName + ".tar.gz", "https://example.test/update.tar.gz", archiveBytes.Length),
            new ReleaseAsset(packageName + ".tar.gz.sha256", "https://example.test/update.tar.gz.sha256", 116));

        var staged = await service.PrepareAsync(release, null, CancellationToken.None);

        Assert.Equal("v1.0.2", staged.Tag);
        Assert.Equal(Path.Combine(env.Paths.AppCache, "updates", "v1.0.2", "extracted", packageName), staged.PackageDirectory);
        Assert.True(File.Exists(staged.ApplyScriptPath));
        Assert.Equal(installed, staged.InstalledExecutablePath);
        Assert.True(File.Exists(staged.LogPath));
        Assert.True(File.Exists(Path.Combine(staged.PackageDirectory, "SHA256SUMS")));
    }

    [Fact]
    public async Task RejectsAnArchiveThatDoesNotMatchItsPublishedChecksumWithoutTouchingTheInstalledApp()
    {
        using var env = new TempEnv();
        await AssertPrepareFailsWithoutBreakingInstalledAppAsync<InvalidDataException>(
            env,
            new AssetHandler(
                Encoding.UTF8.GetBytes("not a tarball"),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  release.tar.gz\n"),
            Release(
                "v1.0.2",
                new ReleaseAsset("release.tar.gz", "https://example.test/update.tar.gz", 13),
                new ReleaseAsset("release.tar.gz.sha256", "https://example.test/update.tar.gz.sha256", 116)),
            "checksum");

        Assert.False(File.Exists(Path.Combine(env.Paths.AppCache, "updates", "v1.0.2", "apply-update.sh")));
    }

    [Fact]
    public async Task RejectsAReleaseWithoutUpdateAssetsWithoutTouchingTheInstalledApp()
    {
        using var env = new TempEnv();
        await AssertPrepareFailsWithoutBreakingInstalledAppAsync<InvalidOperationException>(
            env,
            new StatusHandler(HttpStatusCode.BadRequest),
            Release("v1.0.3"),
            "Linux update archive");
    }

    [Fact]
    public async Task ReportsAMissingRemoteAssetWithoutTouchingTheInstalledApp()
    {
        using var env = new TempEnv();
        await AssertPrepareFailsWithoutBreakingInstalledAppAsync<HttpRequestException>(
            env,
            new StatusHandler(HttpStatusCode.NotFound),
            Release(
                "v1.0.3",
                new ReleaseAsset("release.tar.gz", "https://example.test/update.tar.gz", 13),
                new ReleaseAsset("release.tar.gz.sha256", "https://example.test/update.tar.gz.sha256", 116)),
            "404");
    }

    private static ReleaseCheckResult Release(
        string tag,
        ReleaseAsset? archive = null,
        ReleaseAsset? checksum = null) =>
        new(
            true,
            tag,
            $"https://github.com/polar421/CircuitLordVRModInstaller-linuxinstaller/releases/tag/{tag}",
            null,
            archive,
            checksum);

    private static async Task AssertPrepareFailsWithoutBreakingInstalledAppAsync<TException>(
        TempEnv env,
        HttpMessageHandler handler,
        ReleaseCheckResult release,
        string expectedMessage)
        where TException : Exception
    {
        var installed = CreateInstalledApp(env);
        using var http = new HttpClient(handler);
        var service = new AppUpdateService(
            env.Paths,
            http: http,
            currentProcessPath: () => installed);
        var error = await Assert.ThrowsAsync<TException>(
            () => service.PrepareAsync(release, null, CancellationToken.None));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old-app-ok", RunInstalledApp(installed));
    }

    private static string CreateInstalledApp(TempEnv env)
    {
        var installed = AppUpdateService.InstalledExecutablePath(env.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "#!/usr/bin/env bash\nprintf 'old-app-ok\\n'\n");
        File.SetUnixFileMode(
            installed,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return installed;
    }

    private static string RunInstalledApp(string path)
    {
        var start = new ProcessStartInfo(path)
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
        return output.Trim();
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status));
    }

    private sealed class AssetHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly string _checksum;

        public AssetHandler(byte[] archive, string checksum)
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
