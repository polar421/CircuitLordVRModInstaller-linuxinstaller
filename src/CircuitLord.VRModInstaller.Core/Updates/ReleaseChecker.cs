using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitLord.VRModInstaller.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseCheckResult(
    bool UpdateAvailable,
    string? LatestTag,
    string? ReleaseUrl,
    string? Error,
    ReleaseAsset? Archive = null,
    ReleaseAsset? Checksum = null,
    bool IsPrerelease = false)
{
    /// <summary>True when the release contains both files the updater needs.</summary>
    public bool CanAutoUpdate => Archive is not null && Checksum is not null;
}

/// <summary>
/// Reads the public GitHub release feed for this installer and exposes the
/// verified Linux archive needed by the self-updater.
/// </summary>
public sealed class ReleaseChecker
{
    public const string ReleasesApi =
        "https://api.github.com/repos/polar421/CircuitLordVRModInstaller-linuxinstaller/releases?per_page=10";

    private readonly HttpClient _http;

    public ReleaseChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("CircuitLordVRModInstaller/1.0"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CircuitLordVRModInstaller");
        }
        _http.DefaultRequestHeaders.Accept.TryParseAdd("application/vnd.github+json");
    }

    public Task<ReleaseCheckResult> CheckAsync(Version current, CancellationToken cancellationToken) =>
        CheckAsync(current, includePrereleases: false, cancellationToken);

    public async Task<ReleaseCheckResult> CheckAsync(
        Version current,
        bool includePrereleases,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ReleaseCheckResult(
                    false, null, null, $"GitHub returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ReleaseCheckResult(false, null, null, "GitHub returned an unexpected release list.");
            }

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var tag = release.TryGetProperty("tag_name", out var tagElement)
                    ? tagElement.GetString()
                    : null;
                var url = release.TryGetProperty("html_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var isPrerelease = IsPrereleaseRelease(release, tag);
                if (!includePrereleases && isPrerelease)
                {
                    continue;
                }

                var (archive, checksum) = ReadAssets(release);
                return new ReleaseCheckResult(
                    IsNewer(tag, current), tag, url, null, archive, checksum, isPrerelease);
            }

            return new ReleaseCheckResult(false, null, null, includePrereleases
                ? "No published releases were found."
                : "No published stable releases were found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException or FormatException)
        {
            return new ReleaseCheckResult(false, null, null, ex.Message);
        }
    }

    private static (ReleaseAsset? Archive, ReleaseAsset? Checksum) ReadAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        ReleaseAsset? archive = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)
                || !name.StartsWith("circuitlord-vrmodinstaller-", StringComparison.Ordinal)
                || !name.EndsWith("-linux-x64.tar.gz", StringComparison.Ordinal))
            {
                continue;
            }

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (!IsHttpsUrl(downloadUrl))
            {
                continue;
            }

            var size = 0L;
            if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed))
            {
                size = parsed;
            }

            archive = new ReleaseAsset(name, downloadUrl!, size);
            break;
        }

        if (archive is null)
        {
            return (null, null);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, archive.Name + ".sha256", StringComparison.Ordinal))
            {
                continue;
            }

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (!IsHttpsUrl(downloadUrl))
            {
                return (archive, null);
            }

            var size = 0L;
            if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed))
            {
                size = parsed;
            }
            return (archive, new ReleaseAsset(name!, downloadUrl!, size));
        }

        return (archive, null);
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// GitHub normally sets <c>prerelease</c>, but the tag suffix is also part
    /// of this project's channel convention. Treat either signal as a beta so
    /// a mislabelled release cannot reach a stable install.
    /// </summary>
    private static bool IsPrereleaseRelease(JsonElement release, string tag)
    {
        if (release.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            value = value[..metadata];
        }

        return value.Contains('-');
    }

    /// <summary>
    /// Compares the numeric core of a release tag with the running app version.
    /// A prerelease with a newer core (1.1.0-beta.1) is still an update; an equal
    /// core (1.0.0 vs 1.0.0-beta.1) is treated as the same release line.
    /// </summary>
    public static bool IsNewer(string tag, Version current)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            value = value[..metadata];
        }

        var suffix = value.IndexOf('-');
        var core = suffix >= 0 ? value[..suffix] : value;

        return Version.TryParse(core, out var remote) && remote > current;
    }
}
