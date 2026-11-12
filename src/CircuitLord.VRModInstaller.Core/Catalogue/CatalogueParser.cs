using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Catalogue;

/// <summary>
/// Parses the real output of <c>tf2vr-linux/install.sh --list-versions</c>.
///
/// Verified format (install.sh print_catalogue):
/// <code>
/// Titanfall 2 VR versions:
///   Follow updates:
///     1) latest     1.0.25  2026-10-24  [installed]  &lt;- your choice
///     2) beta       1.0.27  New features, may be unstable
///   Stay on one version:
///     3) 1.0.24     1.0.24  2026-10-23
///     4) beta-1.0.22  1.0.22  beta build
/// </code>
/// A row's second column is the exact token accepted by <c>--version</c>.
/// The printed number is the script's own index and is never reused as the
/// application's ordinal or as a command argument.
/// </summary>
public static partial class CatalogueParser
{
    [GeneratedRegex(@"^\s*(?<ordinal>\d+)\)\s+(?<name>\S+)\s+(?<version>\d+(?:\.\d+)*)(?:\s+(?<rest>.*?))?\s*$")]
    private static partial Regex RowRegex();

    [GeneratedRegex("^\\s{2}(?<heading>Follow updates|Stay on one version):\\s*$")]
    private static partial Regex SectionRegex();

    public static VersionCatalogue Parse(
        string standardOutput,
        string? installedToken,
        DateTimeOffset retrievedAt,
        bool isFromCache = false)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return Empty("The backend returned an empty version catalogue.", retrievedAt, isFromCache);
        }

        // Strip ANSI colour/control sequences without eating ordinary text.
        var cleaned = AnsiRegex().Replace(standardOutput.Replace("\r\n", "\n").Replace('\r', '\n'), string.Empty);

        var rows = new List<Row>();
        var section = string.Empty;
        var sectionSeen = false;

        foreach (var rawLine in cleaned.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var sectionMatch = SectionRegex().Match(line);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups["heading"].Value;
                sectionSeen = true;
                continue;
            }

            var match = RowRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var token = match.Groups["name"].Value.Trim();
            if (!IsValidToken(token))
            {
                // A token we cannot hand to --version safely invalidates the row,
                // not the whole catalogue, but it is never guessed at.
                continue;
            }

            var rest = match.Groups["rest"].Value;
            var isInstalled = false;
            var isChoice = false;

            if (rest.Contains("[installed]", StringComparison.OrdinalIgnoreCase))
            {
                isInstalled = true;
                rest = rest.Replace("[installed]", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            if (rest.Contains("<- your choice", StringComparison.OrdinalIgnoreCase))
            {
                isChoice = true;
                rest = rest.Replace("<- your choice", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            var info = rest.Trim();
            var kind = section.Contains("Stay on one version", StringComparison.Ordinal)
                ? ReleaseKind.Pinned
                : section.Contains("Follow updates", StringComparison.Ordinal)
                    ? ReleaseKind.Moving
                    : DetectKind(token);

            rows.Add(new Row
            {
                Token = token,
                Version = match.Groups["version"].Value,
                Info = info.Length == 0 ? null : info,
                IsInstalled = isInstalled,
                IsSelectedByBackend = isChoice,
                Kind = kind,
                CatalogueIndex = int.TryParse(match.Groups["ordinal"].Value, out var n) ? n : 0,
            });
        }

        if (rows.Count == 0)
        {
            return Empty(
                sectionSeen
                    ? "The backend catalogue headings were found, but no release rows could be parsed."
                    : "The backend catalogue did not contain any recognisable release rows.",
                retrievedAt,
                isFromCache);
        }

        // Duplicate tokens are ambiguous by definition; keep the first and report it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<Row>();
        var duplicates = new List<string>();
        foreach (var row in rows)
        {
            if (seen.Add(row.Token))
            {
                unique.Add(row);
            }
            else
            {
                duplicates.Add(row.Token);
            }
        }

        // Contract: Stable/Latest is choice 1, Beta is choice 2, then the
        // catalogue's own order for everything else. Ordinals are presentation
        // only; each entry keeps its exact backend token.
        var ordered = new List<Row>();
        var latest = unique.FirstOrDefault(r => r.Token.Equals("latest", StringComparison.Ordinal));
        if (latest is not null)
        {
            ordered.Add(latest);
        }
        var beta = unique.FirstOrDefault(r => r.Token.Equals("beta", StringComparison.Ordinal));
        if (beta is not null)
        {
            ordered.Add(beta);
        }
        ordered.AddRange(unique.Where(r => !ReferenceEquals(r, latest) && !ReferenceEquals(r, beta)));

        var entries = ordered.Select((row, index) => new VersionCatalogueEntry
        {
            Ordinal = index + 1,
            BackendToken = row.Token,
            DisplayName = row.Token switch
            {
                "latest" => "Stable / Latest",
                "beta" => "Beta",
                _ => row.Kind == ReleaseKind.Pinned && row.Info is not null && row.Info.EndsWith("build", StringComparison.Ordinal)
                    ? row.Token
                    : row.Token,
            },
            ResolvedVersion = row.Version,
            ChannelId = row.Kind == ReleaseKind.Moving ? row.Token : (row.Token.Contains('-') ? row.Token.Split('-')[0] : null),
            Description = row.Info,
            IsMovingChannel = row.Kind == ReleaseKind.Moving,
            IsPinnedRelease = row.Kind == ReleaseKind.Pinned,
            IsInstalled = row.IsInstalled || (installedToken is not null && row.Token == installedToken),
            IsSelectedByBackend = row.IsSelectedByBackend,
            CatalogueSource = isFromCache ? CatalogueSource.Cached : CatalogueSource.LiveBackend,
        }).ToList();

        var diagnostic = duplicates.Count > 0
            ? "Duplicate catalogue tokens were ignored: " + string.Join(", ", duplicates.Distinct())
            : null;

        return new VersionCatalogue
        {
            Entries = entries,
            RetrievedAt = retrievedAt,
            IsFromCache = isFromCache,
            Diagnostic = diagnostic,
            InstalledToken = installedToken,
        };
    }

    /// <summary>
    /// A token must be a single opaque word. Tokens are never reconstructed from
    /// labels and never allowed to look like an extra command-line option.
    /// </summary>
    public static bool IsValidToken(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && !token.StartsWith('-')
        && token.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c));

    private static ReleaseKind DetectKind(string token) =>
        token is "latest" or "beta" ? ReleaseKind.Moving : ReleaseKind.Pinned;

    private static VersionCatalogue Empty(string diagnostic, DateTimeOffset retrievedAt, bool isFromCache) => new()
    {
        Entries = Array.Empty<VersionCatalogueEntry>(),
        RetrievedAt = retrievedAt,
        IsFromCache = isFromCache,
        Diagnostic = diagnostic,
    };

    [GeneratedRegex("\\x1B\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    private enum ReleaseKind
    {
        Moving,
        Pinned,
    }

    private sealed class Row
    {
        public required string Token { get; init; }
        public required string Version { get; init; }
        public string? Info { get; init; }
        public bool IsInstalled { get; init; }
        public bool IsSelectedByBackend { get; init; }
        public ReleaseKind Kind { get; init; }
        public int CatalogueIndex { get; init; }
    }
}
