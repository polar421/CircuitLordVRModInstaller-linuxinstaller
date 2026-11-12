using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Process;

namespace CircuitLord.VRModInstaller.Core.Updates;

public sealed record AppUpdateProgress(long BytesReceived, long TotalBytes)
{
    public double? Fraction => TotalBytes > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1)
        : null;
}

/// <summary>
/// A verified release package staged in the cache, ready for the detached
/// apply script after the running application exits.
/// </summary>
public sealed record AppUpdatePackage(
    string Tag,
    string PackageDirectory,
    string ApplyScriptPath,
    string InstalledExecutablePath,
    string LogPath);

/// <summary>
/// Downloads, verifies and stages a release archive. The running process is
/// never overwritten directly: <see cref="StartApply"/> starts a helper that
/// waits for this process to exit, runs the package's install.sh and relaunches
/// the installed binary. install.sh performs the atomic app-directory swap, so
/// a failed update leaves the previous app in place.
/// </summary>
public sealed class AppUpdateService
{
    private const int MaxChecksumBytes = 4096;

    private readonly XdgPaths _paths;
    private readonly ProcessRunner _runner;
    private readonly HttpClient _http;
    private readonly Func<string?> _currentProcessPath;

    public AppUpdateService(
        XdgPaths paths,
        ProcessRunner? runner = null,
        HttpClient? http = null,
        Func<string?>? currentProcessPath = null)
    {
        _paths = paths;
        _runner = runner ?? new ProcessRunner();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("CircuitLordVRModInstaller/1.0"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CircuitLordVRModInstaller");
        }
        _currentProcessPath = currentProcessPath ?? (() => Environment.ProcessPath);
    }

    public static string InstalledExecutablePath(XdgPaths paths) =>
        Path.Combine(paths.AppData, "app", "CircuitLordVRModInstaller");

    public bool IsInstalledCopy() => IsInstalledCopy(_paths, _currentProcessPath());

    public static bool IsInstalledCopy(XdgPaths paths, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            var installed = Path.GetFullPath(InstalledExecutablePath(paths));
            var current = Path.GetFullPath(processPath);
            // Resolve both sides: XDG data directories may themselves be
            // symlinked, and launchers commonly invoke the bin symlink.
            var installedReal = File.ResolveLinkTarget(installed, returnFinalTarget: true)?.FullName ?? installed;
            var currentReal = File.ResolveLinkTarget(current, returnFinalTarget: true)?.FullName ?? current;
            return string.Equals(current, installed, StringComparison.Ordinal)
                   || string.Equals(currentReal, installedReal, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public async Task<AppUpdatePackage> PrepareAsync(
        ReleaseCheckResult release,
        Action<AppUpdateProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        if (!release.UpdateAvailable)
        {
            throw new InvalidOperationException("The selected release is not newer than this application.");
        }
        if (!release.CanAutoUpdate)
        {
            throw new InvalidOperationException("The release does not include a Linux update archive and checksum.");
        }
        if (!IsInstalledCopy())
        {
            throw new InvalidOperationException(
                "Automatic installation is only available from the installed application.");
        }

        var tag = release.LatestTag?.Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidOperationException("The release does not have a usable version tag.");
        }

        var safeTag = new string(tag.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        if (safeTag.Length == 0)
        {
            throw new InvalidOperationException("The release tag cannot be used as a cache directory.");
        }

        var updateRoot = Path.Combine(_paths.AppCache, "updates", safeTag);
        if (Directory.Exists(updateRoot))
        {
            Directory.Delete(updateRoot, recursive: true);
        }
        Directory.CreateDirectory(updateRoot);

        EnsureHttps(release.Checksum!);
        EnsureHttps(release.Archive!);

        var checksumText = await DownloadStringAsync(release.Checksum!, cancellationToken).ConfigureAwait(false);
        var expectedChecksum = ParseChecksum(checksumText);

        var archivePath = Path.Combine(updateRoot, "release.tar.gz");
        await DownloadFileAsync(release.Archive!, archivePath, onProgress, cancellationToken).ConfigureAwait(false);

        var actualChecksum = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded release archive does not match the published SHA-256 checksum.");
        }

        var extractRoot = Path.Combine(updateRoot, "extracted");
        Directory.CreateDirectory(extractRoot);
        await ValidateArchivePathsAsync(archivePath, updateRoot, cancellationToken).ConfigureAwait(false);
        await ExtractArchiveAsync(archivePath, extractRoot, updateRoot, cancellationToken).ConfigureAwait(false);

        var packageRoot = FindPackageRoot(extractRoot);
        await VerifyInnerChecksumsAsync(packageRoot, cancellationToken).ConfigureAwait(false);

        var applyScript = Path.Combine(updateRoot, "apply-update.sh");
        await File.WriteAllTextAsync(applyScript, ApplyScript, cancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(
            applyScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var logPath = Path.Combine(_paths.AppState, "app-update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        if (!File.Exists(logPath))
        {
            await File.WriteAllTextAsync(logPath, string.Empty, cancellationToken).ConfigureAwait(false);
        }

        return new AppUpdatePackage(
            tag,
            packageRoot,
            applyScript,
            InstalledExecutablePath(_paths),
            logPath);
    }

    /// <summary>
    /// Starts a detached helper. It waits for this process, runs the verified
    /// package's install.sh and relaunches the installed binary.
    /// </summary>
    public System.Diagnostics.Process StartApply(AppUpdatePackage package, int currentProcessId)
    {
        var bash = File.Exists("/bin/bash") ? "/bin/bash" : "/usr/bin/bash";
        var psi = new ProcessStartInfo(bash)
        {
            WorkingDirectory = package.PackageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        if (setsid is not null)
        {
            psi.FileName = setsid;
            psi.ArgumentList.Add(bash);
        }

        psi.ArgumentList.Add(package.ApplyScriptPath);
        psi.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(package.PackageDirectory);
        psi.ArgumentList.Add(package.InstalledExecutablePath);
        psi.ArgumentList.Add(package.LogPath);

        return System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("The update helper could not be started.");
    }

    public static string ParseChecksum(string text)
    {
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null
            || token.Length != 64
            || token.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException("The published checksum file is not a SHA-256 digest.");
        }
        return token.ToLowerInvariant();
    }

    private async Task<string> DownloadStringAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxChecksumBytes)
        {
            throw new InvalidDataException("The published checksum file is unexpectedly large.");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > MaxChecksumBytes)
        {
            throw new InvalidDataException("The published checksum file is unexpectedly large.");
        }
        return text;
    }

    private async Task DownloadFileAsync(
        ReleaseAsset asset,
        string destination,
        Action<AppUpdateProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength
            ?? (asset.Size > 0 ? asset.Size : 0);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            onProgress?.Invoke(new AppUpdateProgress(received, total));
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task ValidateArchivePathsAsync(
        string archivePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var listing = await _runner.RunAsync(
            "tar",
            new[] { "-tzf", archivePath },
            workingDirectory,
            timeout: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!listing.Succeeded)
        {
            throw new InvalidDataException("The update archive could not be read: " + listing.StandardError.Trim());
        }

        foreach (var raw in listing.StandardOutput.Split('\n'))
        {
            var entry = raw.TrimEnd('\r').Replace('\\', '/');
            if (entry.Length == 0)
            {
                continue;
            }
            if (entry.StartsWith('/') || entry.Split('/').Any(part => part == ".."))
            {
                throw new InvalidDataException("The update archive contains a path outside the package.");
            }
        }
    }

    private async Task ExtractArchiveAsync(
        string archivePath,
        string extractRoot,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "tar",
            new[] { "-xzf", archivePath, "-C", extractRoot },
            workingDirectory,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidDataException("The update archive could not be extracted: " + result.StandardError.Trim());
        }
    }

    private static string FindPackageRoot(string extractRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(extractRoot))
        {
            if (File.Exists(Path.Combine(directory, "install.sh"))
                && File.Exists(Path.Combine(directory, "SHA256SUMS"))
                && File.Exists(Path.Combine(directory, "app", "CircuitLordVRModInstaller")))
            {
                return directory;
            }
        }

        throw new InvalidDataException("The update archive does not contain a complete installer package.");
    }

    private async Task VerifyInnerChecksumsAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "sha256sum",
            new[] { "--check", "--strict", "--quiet", "SHA256SUMS" },
            packageRoot,
            timeout: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidDataException(
                "The downloaded files do not match the release's SHA256SUMS.");
        }
    }

    private static void EnsureHttps(ReleaseAsset asset)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The update asset does not use HTTPS.");
        }
    }

    private const string ApplyScript = """
        #!/usr/bin/env bash
        set -u

        old_pid="$1"
        package_dir="$2"
        app_bin="$3"
        log_file="$4"

        exec >>"$log_file" 2>&1
        echo "=== Installer update $(date -Is) ==="

        # The app must exit before install.sh swaps the app directory. This also
        # means the running process keeps a mapped copy of the old binary until
        # the helper is ready to relaunch the new one.
        for ((attempt = 0; attempt < 300; attempt++)); do
            if ! kill -0 "$old_pid" 2>/dev/null; then
                break
            fi
            sleep 0.2
        done

        if kill -0 "$old_pid" 2>/dev/null; then
            echo "The running app did not exit; update aborted."
            exit 1
        fi

        bash "$package_dir/install.sh"
        status=$?
        if [ "$status" -ne 0 ]; then
            echo "install.sh failed with exit code $status; the previous app remains in place."
        elif [ ! -x "$app_bin" ]; then
            echo "The updated app binary is missing or not executable: $app_bin"
            status=1
        fi

        cd "$HOME" 2>/dev/null || cd /
        if command -v setsid >/dev/null 2>&1; then
            setsid "$app_bin" >/dev/null 2>&1 < /dev/null &
        else
            nohup "$app_bin" >/dev/null 2>&1 < /dev/null &
        fi

        if [ "$status" -eq 0 ]; then
            echo "Updated application relaunched."
        else
            echo "Previous application relaunched after the failed update."
        fi
        exit "$status"
        """;
}
