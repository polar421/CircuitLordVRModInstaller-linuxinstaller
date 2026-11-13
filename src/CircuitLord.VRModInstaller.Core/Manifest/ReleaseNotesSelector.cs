using System;
using System.Collections.Generic;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Manifest;

public enum ReleaseNotesSource
{
    None,
    SelectedRelease,
    InstalledRelease,
    StableManifest,
}

/// <summary>
/// The changelog entry that matches the release the window is describing.
/// The manifest has one flat list of changes, so the caller also needs the
/// source to label stable fallback notes correctly.
/// </summary>
public sealed record ReleaseNotesSelection
{
    public static ReleaseNotesSelection None { get; } = new();

    public ReleaseNotesSource Source { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<ManifestChange> Changes { get; init; } = Array.Empty<ManifestChange>();
    public bool HasNotes => Changes.Count > 0;
}

public static class ReleaseNotesSelector
{
    public static ReleaseNotesSelection Select(
        TitanfallManifestProduct product,
        string? selectedVersion,
        string? installedVersion)
    {
        var selected = Find(product.Changes, selectedVersion);
        if (selected is not null)
        {
            return From(ReleaseNotesSource.SelectedRelease, selected);
        }

        var installed = Find(product.Changes, installedVersion);
        if (installed is not null)
        {
            return From(ReleaseNotesSource.InstalledRelease, installed);
        }

        // The top-level changes list belongs to the stable manifest. Prefer the
        // entry for the current stable version, but never drop a changelog just
        // because its version header differs from that version.
        var stable = Find(product.Changes, product.Version) ?? product.Changes.FirstOrDefault();
        return stable is null
            ? ReleaseNotesSelection.None
            : From(ReleaseNotesSource.StableManifest, stable);
    }

    /// <summary>Finds the exact changelog entry for one display version.</summary>
    public static ManifestChange? FindForVersion(TitanfallManifestProduct product, string? version)
        => Find(product.Changes, version);

    private static ReleaseNotesSelection From(ReleaseNotesSource source, ManifestChange change) => new()
    {
        Source = source,
        Version = change.Version,
        Changes = new[] { change },
    };

    private static ManifestChange? Find(IReadOnlyList<ManifestChange> changes, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return changes.FirstOrDefault(change => VersionsEqual(change.Version, version));
    }

    private static bool VersionsEqual(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
            || (Version.TryParse(normalizedLeft, out var leftVersion)
                && Version.TryParse(normalizedRight, out var rightVersion)
                && leftVersion == rightVersion);
    }

    private static string Normalize(string? version)
    {
        var trimmed = version?.Trim() ?? string.Empty;
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }
}
