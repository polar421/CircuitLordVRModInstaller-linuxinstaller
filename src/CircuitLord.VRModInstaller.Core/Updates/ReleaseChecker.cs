using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitLord.VRModInstaller.Core.Updates;

public sealed record ReleaseCheckResult(
    bool UpdateAvailable,
    string? LatestTag,
    string? ReleaseUrl,
    string? Error);

/// <summary>
/// Reads the public GitHub release feed for this installer. It only reports a
/// newer tag; downloading and replacing the running app stays a user action.
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

    public async Task<ReleaseCheckResult> CheckAsync(Version current, CancellationToken cancellationToken)
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

                return new ReleaseCheckResult(IsNewer(tag, current), tag, url, null);
            }

            return new ReleaseCheckResult(false, null, null, "No published releases were found.");
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
