using System;
using System.Linq;

namespace CircuitLord.VRModInstaller.Core.Releases;

/// <summary>
/// Orders release versions using numeric components and semantic-version
/// prerelease precedence. Catalogue labels are never sorted as plain strings.
/// </summary>
public static class ReleaseVersionComparer
{
    public static int Compare(string? left, string? right)
        => CompareParsed(Parse(left), Parse(right), left, right);

    private static int CompareParsed(
        ParsedVersion left,
        ParsedVersion right,
        string? leftText,
        string? rightText)
    {
        if (-1 == left.Valid || -1 == right.Valid)
        {
            return string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
        }

        var core = CompareCores(left.Core, right.Core);
        if (core != 0)
        {
            return core;
        }

        // A release without a prerelease suffix sorts after its prereleases.
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
        {
            return right.Prerelease.Length.CompareTo(left.Prerelease.Length);
        }

        var count = Math.Min(left.Prerelease.Length, right.Prerelease.Length);
        for (var i = 0; i < count; i++)
        {
            var identifier = CompareIdentifiers(left.Prerelease[i], right.Prerelease[i]);
            if (identifier != 0)
            {
                return identifier;
            }
        }

        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private static int CompareCores(Version left, Version right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = Component(left.Build).CompareTo(Component(right.Build));
        return result != 0 ? result : Component(left.Revision).CompareTo(Component(right.Revision));
    }

    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = int.TryParse(left, out _);
        var rightNumeric = int.TryParse(right, out _);
        if (leftNumeric && rightNumeric)
        {
            var leftValue = long.Parse(left);
            var rightValue = long.Parse(right);
            return leftValue.CompareTo(rightValue);
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return string.CompareOrdinal(left, right);
    }

    private static ParsedVersion Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            value = value[..metadata];
        }

        var prerelease = Array.Empty<string>();
        var separator = value.IndexOf('-');
        if (separator >= 0)
        {
            prerelease = value[(separator + 1)..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();
            value = value[..separator];
        }

        if (!Version.TryParse(value, out var core))
        {
            return new ParsedVersion(-1, new Version(0, 0), Array.Empty<string>());
        }

        return new ParsedVersion(1, core, prerelease);
    }

    private static int Component(int value) => value < 0 ? 0 : value;

    private readonly record struct ParsedVersion(int Valid, Version Core, string[] Prerelease);
}
