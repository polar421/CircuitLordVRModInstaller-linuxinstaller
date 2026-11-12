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

    public Task<ReleaseCheckResult> CheckAsync(
        Version current,
        bool includePrereleases,
        CancellationToken cancellationToken) =>
        CheckAsync(current.ToString(), includePrereleases, cancellationToken);

    /// <summary>
    /// Checks releases against a version that may include a prerelease suffix,
    /// such as <c>1.0.7-beta.2</c>. The numeric-only overload cannot distinguish
    /// two builds on the same core version.
    /// </summary>
    public async Task<ReleaseCheckResult> CheckAsync(
        string currentVersion,
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
                    IsNewer(tag, currentVersion), tag, url, null, archive, checksum, isPrerelease);
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
    /// Compares a release tag with a numeric running version. This overload is
    /// suitable for stable builds; beta builds should pass their full
    /// informational version so the prerelease suffix is compared too.
    /// </summary>
    public static bool IsNewer(string tag, Version current) =>
        IsNewer(tag, current.ToString());

    /// <summary>
    /// Compares release tags using semantic-version precedence, including
    /// prerelease identifiers. A stable release sorts above a prerelease with
    /// the same numeric core, so <c>1.0.7</c> is newer than <c>1.0.7-beta.2</c>.
    /// </summary>
    public static bool IsNewer(string tag, string currentVersion)
    {
        if (!TryParseVersion(tag, out var remoteCore, out var remotePrerelease)
            || !TryParseVersion(currentVersion, out var currentCore, out var currentPrerelease))
        {
            return false;
        }

        var core = CompareCores(remoteCore, currentCore);
        return core != 0
            ? core > 0
            : ComparePrereleases(remotePrerelease, currentPrerelease) > 0;
    }

    private static bool TryParseVersion(
        string value,
        out Version core,
        out string[] prerelease)
    {
        core = new Version(0, 0);
        prerelease = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
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
        var coreText = suffix >= 0 ? value[..suffix] : value;
        var prereleaseText = suffix >= 0 ? value[(suffix + 1)..] : string.Empty;
        if (!Version.TryParse(coreText, out var parsedCore) || parsedCore is null)
        {
            return false;
        }
        core = parsedCore;

        if (prereleaseText.Length == 0)
        {
            return true;
        }

        prerelease = prereleaseText.Split('.');
        foreach (var identifier in prerelease)
        {
            if (identifier.Length == 0)
            {
                prerelease = Array.Empty<string>();
                return false;
            }
        }

        return true;
    }

    private static int CompareCores(Version left, Version right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;

        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;

        // Assembly versions are four-part (1.0.7.0), while release tags are
        // usually three-part (1.0.7). Treat omitted components as zero.
        result = Component(left.Build).CompareTo(Component(right.Build));
        return result != 0 ? result : Component(left.Revision).CompareTo(Component(right.Revision));
    }

    private static int Component(int value) => value < 0 ? 0 : value;

    private static int ComparePrereleases(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var result = ComparePrereleaseIdentifier(left[i], right[i]);
            if (result != 0) return result;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric)
        {
            // Compare arbitrary-length numeric identifiers without overflowing.
            left = TrimLeadingZeros(left);
            right = TrimLeadingZeros(right);
            var length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : string.CompareOrdinal(left, right);
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value)
    {
        if (value.Length == 0) return false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9') return false;
        }
        return true;
    }

    private static string TrimLeadingZeros(string value)
    {
        var trimmed = value.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
