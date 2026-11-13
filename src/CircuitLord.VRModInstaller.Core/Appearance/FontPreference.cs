using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitLord.VRModInstaller.Core.Appearance;

/// <summary>A font that has been verified as loadable before it is offered.</summary>
public sealed record FontChoice(string Id, string DisplayName, bool IsMonospace, bool IsBundled)
{
    public override string ToString() => DisplayName;
}

/// <summary>The result of matching a saved font identifier against the current catalog.</summary>
public readonly record struct FontResolution(
    string EffectiveFontId,
    bool IsUnavailable,
    string? SavedFontId);

/// <summary>
/// Stable font identifiers stored in settings. Display names and font sources may
/// change between sessions, but these identifiers remain the source of truth.
/// </summary>
public static class FontPreference
{
    public const string DefaultUiFontId = "bundled:liberation-serif";
    public const string BundledNotoSansCjkJpId = "bundled:noto-sans-cjk-jp";
    public const string BundledNotoSansCjkScId = "bundled:noto-sans-cjk-sc";
    public const string FollowInterfaceFontId = "interface";

    private const string SystemPrefix = "system:";

    private static readonly IReadOnlyDictionary<string, string> BundledFamilyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultUiFontId] = "Liberation Serif",
            [BundledNotoSansCjkJpId] = "Noto Sans CJK JP",
            [BundledNotoSansCjkScId] = "Noto Sans CJK SC",
        };

    public static IReadOnlyList<FontChoice> BundledFonts { get; } =
    [
        new(DefaultUiFontId, "Liberation Serif", IsMonospace: false, IsBundled: true),
        new(BundledNotoSansCjkJpId, "Noto Sans CJK JP", IsMonospace: false, IsBundled: true),
        new(BundledNotoSansCjkScId, "Noto Sans CJK SC", IsMonospace: false, IsBundled: true),
    ];

    public static string SystemFontId(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            throw new ArgumentException("A system font family name is required.", nameof(familyName));
        }

        return SystemPrefix + familyName.Trim();
    }

    public static string NormalizeUiFont(string? fontId)
        => NormalizeFontId(fontId, DefaultUiFontId);

    public static string NormalizeTechnicalFont(string? fontId)
    {
        var trimmed = fontId?.Trim();
        return string.Equals(trimmed, FollowInterfaceFontId, StringComparison.OrdinalIgnoreCase)
            ? FollowInterfaceFontId
            : NormalizeFontId(trimmed, FollowInterfaceFontId);
    }

    public static bool IsFontId(string? fontId)
        => TryGetBundledFamilyName(fontId, out _) || TryGetSystemFamilyName(fontId, out _);

    public static bool TryGetBundledFamilyName(string? fontId, out string familyName)
    {
        familyName = string.Empty;
        if (string.IsNullOrWhiteSpace(fontId))
        {
            return false;
        }

        if (!BundledFamilyNames.TryGetValue(fontId.Trim(), out var name))
        {
            return false;
        }

        familyName = name;
        return true;
    }

    public static bool TryGetSystemFamilyName(string? fontId, out string familyName)
    {
        familyName = string.Empty;
        if (string.IsNullOrWhiteSpace(fontId))
        {
            return false;
        }

        var trimmed = fontId.Trim();
        if (!trimmed.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = trimmed[SystemPrefix.Length..].Trim();
        if (name.Length == 0)
        {
            return false;
        }

        familyName = name;
        return true;
    }

    public static string DescribeFontId(string? fontId)
    {
        if (TryGetBundledFamilyName(fontId, out var bundledName))
        {
            return bundledName;
        }

        if (TryGetSystemFamilyName(fontId, out var systemName))
        {
            return systemName;
        }

        return fontId?.Trim() ?? string.Empty;
    }

    public static FontResolution ResolveUiFont(string? savedFontId, IEnumerable<string> availableFontIds)
        => ResolveAvailability(
            NormalizeUiFont(savedFontId),
            availableFontIds,
            DefaultUiFontId);

    public static FontResolution ResolveTechnicalFont(string? savedFontId, IEnumerable<string> availableFontIds)
    {
        var normalized = NormalizeTechnicalFont(savedFontId);
        if (normalized == FollowInterfaceFontId)
        {
            return new FontResolution(FollowInterfaceFontId, IsUnavailable: false, SavedFontId: null);
        }

        return ResolveAvailability(normalized, availableFontIds, FollowInterfaceFontId);
    }

    private static string NormalizeFontId(string? fontId, string fallback)
    {
        var trimmed = fontId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        if (TryGetBundledFamilyName(trimmed, out _))
        {
            // Canonicalize IDs entered with different casing.
            return BundledFamilyNames.Keys.First(id => string.Equals(id, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        if (TryGetSystemFamilyName(trimmed, out var familyName))
        {
            return SystemFontId(familyName);
        }

        return fallback;
    }

    private static FontResolution ResolveAvailability(
        string normalizedFontId,
        IEnumerable<string> availableFontIds,
        string fallbackFontId)
    {
        var match = availableFontIds.FirstOrDefault(id =>
            string.Equals(id, normalizedFontId, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? new FontResolution(fallbackFontId, IsUnavailable: true, SavedFontId: normalizedFontId)
            : new FontResolution(match, IsUnavailable: false, SavedFontId: null);
    }
}
