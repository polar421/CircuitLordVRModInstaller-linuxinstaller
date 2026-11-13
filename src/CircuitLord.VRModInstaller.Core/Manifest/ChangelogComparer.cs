using System;
using System.Collections.Generic;
using System.Linq;
namespace CircuitLord.VRModInstaller.Core.Manifest;

/// <summary>
/// Line-level comparison for two release changelogs. Availability is explicit:
/// a failed lookup or the localized placeholder shown in the UI is never
/// treated as a changed line.
/// </summary>
public sealed record ChangelogComparison(
    bool IsAvailable,
    IReadOnlyList<string> OnlyInLeft,
    IReadOnlyList<string> OnlyInRight)
{
    public static ChangelogComparison Unavailable { get; } = new(
        false,
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool IsIdentical => IsAvailable && OnlyInLeft.Count == 0 && OnlyInRight.Count == 0;
}

public static class ChangelogComparer
{
    public static ChangelogComparison Compare(
        string? leftText,
        string? rightText,
        string? unavailablePlaceholder = null)
    {
        if (IsUnavailable(leftText, unavailablePlaceholder)
            || IsUnavailable(rightText, unavailablePlaceholder))
        {
            return ChangelogComparison.Unavailable;
        }

        var left = Lines(leftText!);
        var right = Lines(rightText!);
        if (left.Count == 0 || right.Count == 0)
        {
            return ChangelogComparison.Unavailable;
        }

        var leftSet = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
        var rightSet = new HashSet<string>(right, StringComparer.OrdinalIgnoreCase);
        var onlyLeft = left
            .Where(line => !rightSet.Contains(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onlyRight = right
            .Where(line => !leftSet.Contains(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ChangelogComparison(true, onlyLeft, onlyRight);
    }

    private static bool IsUnavailable(string? text, string? placeholder)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return placeholder is not null
            && string.Equals(text.Trim(), placeholder.Trim(), StringComparison.Ordinal);
    }

    private static List<string> Lines(string text) => text
        .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();
}
