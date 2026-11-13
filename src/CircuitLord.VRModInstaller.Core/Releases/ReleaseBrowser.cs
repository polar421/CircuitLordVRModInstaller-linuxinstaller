using System;
using System.Collections.Generic;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Releases;

public enum ReleaseFilter
{
    All,
    Stable,
    Beta,
    Pinned,
    Installed,
    Selected,
}

public enum ReleaseSort
{
    CatalogueOrder,
    NewestFirst,
    OldestFirst,
    StableFirst,
    BetaFirst,
}

/// <summary>
/// One catalogue entry plus the optional manifest metadata used by the
/// release explorer. The backend token remains the only install authority.
/// </summary>
public sealed record ReleaseBrowserItem
{
    public required VersionCatalogueEntry Entry { get; init; }
    public required string ChannelKey { get; init; }
    public string ChannelLabel { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ManifestDescription { get; init; }
    public string? Changelog { get; init; }
    public string? LastUpdated { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsSelected { get; init; }

    public string Token => Entry.BackendToken;
    public string Version => Entry.ResolvedVersion ?? string.Empty;
    public string DisplayName => Entry.DisplayName;
    public bool IsPinned => Entry.IsPinnedRelease;
    public bool IsCached => Entry.CatalogueSource == CatalogueSource.Cached;
}

public static class ReleaseBrowser
{
    public static IReadOnlyList<ReleaseBrowserItem> Build(
        VersionCatalogue catalogue,
        string? installedToken,
        string? selectedToken,
        Func<VersionCatalogueEntry, string?>? changelog = null,
        Func<VersionCatalogueEntry, string?>? description = null,
        Func<VersionCatalogueEntry, string?>? manifestDescription = null,
        Func<VersionCatalogueEntry, string?>? lastUpdated = null)
    {
        return catalogue.Entries
            .Select(entry => new ReleaseBrowserItem
            {
                Entry = entry,
                ChannelKey = ChannelKeyFor(entry),
                Description = description?.Invoke(entry) ?? entry.Description,
                ManifestDescription = manifestDescription?.Invoke(entry),
                Changelog = changelog?.Invoke(entry),
                LastUpdated = lastUpdated?.Invoke(entry),
                IsInstalled = entry.IsInstalled
                    || (!string.IsNullOrWhiteSpace(installedToken)
                        && string.Equals(entry.BackendToken, installedToken, StringComparison.Ordinal)),
                IsSelected = !string.IsNullOrWhiteSpace(selectedToken)
                    && string.Equals(entry.BackendToken, selectedToken, StringComparison.Ordinal),
            })
            .ToList();
    }

    /// <summary>
    /// Groups moving channels by their real channel id, while every pinned
    /// release stays in the pinned group regardless of its name or token.
    /// </summary>
    public static string ChannelKeyFor(VersionCatalogueEntry entry)
    {
        if (entry.IsPinnedRelease)
        {
            return "pinned";
        }
        if (string.Equals(entry.BackendToken, "latest", StringComparison.Ordinal)
            || string.Equals(entry.ChannelId, "latest", StringComparison.Ordinal))
        {
            return "stable";
        }
        if (string.Equals(entry.BackendToken, "beta", StringComparison.Ordinal)
            || string.Equals(entry.ChannelId, "beta", StringComparison.Ordinal))
        {
            return "beta";
        }
        return entry.IsMovingChannel
            ? entry.ChannelId ?? entry.BackendToken
            : "pinned";
    }

    public static IReadOnlyList<ReleaseBrowserItem> Filter(
        IEnumerable<ReleaseBrowserItem> items,
        ReleaseFilter filter,
        string? query)
    {
        var normalized = NormalizeQuery(query);
        return items
            .Where(item => filter switch
            {
                ReleaseFilter.Stable => item.ChannelKey == "stable",
                ReleaseFilter.Beta => item.ChannelKey == "beta",
                ReleaseFilter.Pinned => item.ChannelKey == "pinned",
                ReleaseFilter.Installed => item.IsInstalled,
                ReleaseFilter.Selected => item.IsSelected,
                _ => true,
            })
            .Where(item => normalized.Length == 0 || Matches(item, normalized))
            .ToList();
    }

    public static IReadOnlyList<ReleaseBrowserItem> Sort(
        IEnumerable<ReleaseBrowserItem> items,
        ReleaseSort sort)
    {
        var source = items.ToList();
        return sort switch
        {
            ReleaseSort.NewestFirst => source
                .OrderByDescending(item => item.Version, VersionComparer.Instance)
                .ThenBy(item => item.Entry.Ordinal)
                .ToList(),
            ReleaseSort.OldestFirst => source
                .OrderBy(item => item.Version, VersionComparer.Instance)
                .ThenBy(item => item.Entry.Ordinal)
                .ToList(),
            ReleaseSort.StableFirst => source
                .OrderBy(item => ChannelRank(item.ChannelKey, stableFirst: true))
                .ThenBy(item => item.Entry.Ordinal)
                .ToList(),
            ReleaseSort.BetaFirst => source
                .OrderBy(item => ChannelRank(item.ChannelKey, stableFirst: false))
                .ThenBy(item => item.Entry.Ordinal)
                .ToList(),
            _ => source.OrderBy(item => item.Entry.Ordinal).ToList(),
        };
    }

    public static bool Matches(ReleaseBrowserItem item, string? query)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0)
        {
            return true;
        }

        return Contains(item.Version, normalized)
            || Contains(item.DisplayName, normalized)
            || Contains(item.Token, normalized)
            || Contains(item.ChannelKey, normalized)
            || Contains(item.ChannelLabel, normalized)
            || Contains(item.Description, normalized)
            || Contains(item.ManifestDescription, normalized)
            || Contains(item.Changelog, normalized);
    }

    private static int ChannelRank(string channel, bool stableFirst)
    {
        if (stableFirst)
        {
            return channel switch
            {
                "stable" => 0,
                "beta" => 1,
                "pinned" => 2,
                _ => 3,
            };
        }
        return channel switch
        {
            "beta" => 0,
            "stable" => 1,
            "pinned" => 2,
            _ => 3,
        };
    }

    private static string NormalizeQuery(string? query)
    {
        var text = (query ?? string.Empty).Trim();
        return text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
    }

    private static bool Contains(string? value, string query)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private sealed class VersionComparer : IComparer<string>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
            => ReleaseVersionComparer.Compare(x, y);
    }
}
