using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Manifest;

/// <summary>
/// Retrieves CircuitLord's official manifest (release metadata only).
/// The backend catalogue remains the authority for which tokens can actually be
/// installed; the manifest supplies versions, channels and changelogs.
///
/// Never throws: network or schema problems degrade to a cached or empty
/// snapshot with a visible warning.
/// </summary>
public sealed class ManifestService
{
    public const string ManifestUri =
        "https://raw.githubusercontent.com/CircuitLord/CircuitLordVRModInstaller/main/manifest-v3.json";

    private const int MaxResponseBytes = 1_048_576;
    private readonly XdgPaths _paths;
    private readonly HttpClient _http;

    public ManifestService(XdgPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ManifestSnapshot> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_paths.AppCache);
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
            request.Headers.UserAgent.ParseAdd("CircuitLordVRModInstaller-Linux/1.0");

            if (!forceRefresh && File.Exists(_paths.ManifestCacheFile))
            {
                var etagFile = _paths.ManifestCacheFile + ".etag";
                if (File.Exists(etagFile))
                {
                    var etag = File.ReadAllText(etagFile).Trim();
                    if (etag.Length > 0)
                    {
                        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
                    }
                }
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && File.Exists(_paths.ManifestCacheFile))
            {
                return Parse(await File.ReadAllTextAsync(_paths.ManifestCacheFile, cancellationToken).ConfigureAwait(false), fromCache: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return FromCacheOrEmpty($"The manifest request failed with HTTP {(int)response.StatusCode}.");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxResponseBytes)
            {
                return FromCacheOrEmpty("The manifest response was larger than the accepted limit.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > MaxResponseBytes)
            {
                return FromCacheOrEmpty("The manifest response was larger than the accepted limit.");
            }

            WriteAtomically(_paths.ManifestCacheFile, body);
            if (response.Headers.ETag is { } etagValue)
            {
                WriteAtomically(_paths.ManifestCacheFile + ".etag", etagValue.ToString());
            }

            return Parse(body, fromCache: false);
        }
        catch (OperationCanceledException)
        {
            return FromCacheOrEmpty("The manifest request was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException)
        {
            return FromCacheOrEmpty("The manifest could not be read: " + ex.Message);
        }
    }

    public ManifestSnapshot Parse(string json, bool fromCache)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32,
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(fromCache, "The manifest root is not a JSON object.");
            }

            var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var v) ? v : 0;
            if (!root.TryGetProperty("titanfall2vr", out var product) || product.ValueKind != JsonValueKind.Object)
            {
                return Invalid(fromCache, "The manifest has no titanfall2vr product entry.");
            }

            if (!product.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
            {
                return Invalid(fromCache, "The titanfall2vr entry has no readable version.");
            }

            var channels = new List<ManifestChannel>();
            if (product.TryGetProperty("channels", out var channelsElement) && channelsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var channel in channelsElement.EnumerateArray())
                {
                    if (channel.ValueKind != JsonValueKind.Object
                        || !channel.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                        || !channel.TryGetProperty("version", out var cv) || cv.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    channels.Add(new ManifestChannel
                    {
                        Id = Truncate(id.GetString() ?? string.Empty, 64),
                        Description = channel.TryGetProperty("description", out var cd) && cd.ValueKind == JsonValueKind.String
                            ? Truncate(cd.GetString() ?? string.Empty, 400)
                            : null,
                        Version = Truncate(cv.GetString() ?? string.Empty, 64),
                    });
                }
            }

            var changes = new List<ManifestChange>();
            if (product.TryGetProperty("changes", out var changesElement) && changesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var change in changesElement.EnumerateArray())
                {
                    if (change.ValueKind != JsonValueKind.Object
                        || !change.TryGetProperty("version", out var cver) || cver.ValueKind != JsonValueKind.String
                        || !change.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.String)
                    {
                        // A malformed optional changelog entry never invalidates the snapshot.
                        continue;
                    }
                    changes.Add(new ManifestChange
                    {
                        Version = Truncate(cver.GetString() ?? string.Empty, 64),
                        Date = change.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                            ? Truncate(d.GetString() ?? string.Empty, 32)
                            : null,
                        Notes = Truncate(notes.GetString() ?? string.Empty, 8000),
                    });
                }
            }

            return new ManifestSnapshot
            {
                SchemaVersion = schemaVersion,
                SourceUri = ManifestUri,
                RetrievedAt = DateTimeOffset.Now,
                IsFromCache = fromCache,
                RawContentHash = Hash(json),
                Titanfall2Vr = new TitanfallManifestProduct
                {
                    Id = ReadString(product, "id") ?? "Titanfall2VR",
                    Name = ReadString(product, "name") ?? "Titanfall 2 VR",
                    Description = ReadString(product, "description"),
                    Version = Truncate(versionElement.GetString() ?? string.Empty, 64),
                    Url = ReadString(product, "url"),
                    Sha256 = ReadString(product, "sha256"),
                    Size = product.TryGetProperty("size", out var size) && size.TryGetInt64(out var s) ? s : null,
                    Channels = channels,
                    Changes = changes,
                },
            };
        }
        catch (JsonException ex)
        {
            return Invalid(fromCache, "The manifest is not valid JSON: " + ex.Message);
        }
    }

    private ManifestSnapshot FromCacheOrEmpty(string warning)
    {
        if (File.Exists(_paths.ManifestCacheFile))
        {
            try
            {
                var cached = Parse(File.ReadAllText(_paths.ManifestCacheFile), fromCache: true);
                return new ManifestSnapshot
                {
                    SchemaVersion = cached.SchemaVersion,
                    SourceUri = ManifestUri,
                    RetrievedAt = cached.RetrievedAt,
                    IsFromCache = true,
                    RawContentHash = cached.RawContentHash,
                    Titanfall2Vr = cached.Titanfall2Vr,
                    ValidationWarning = warning + " Showing cached metadata.",
                };
            }
            catch (IOException)
            {
                // Fall through to the empty snapshot.
            }
        }

        return Invalid(true, warning);
    }

    private static ManifestSnapshot Invalid(bool fromCache, string warning) => new()
    {
        SourceUri = ManifestUri,
        RetrievedAt = DateTimeOffset.Now,
        IsFromCache = fromCache,
        ValidationWarning = warning,
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Truncate(value.GetString() ?? string.Empty, 2000)
            : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
