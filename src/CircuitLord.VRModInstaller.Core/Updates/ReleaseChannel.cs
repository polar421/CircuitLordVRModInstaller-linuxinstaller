namespace CircuitLord.VRModInstaller.Core.Updates;

/// <summary>Which GitHub release line the updater is allowed to follow.</summary>
public enum ReleaseChannel
{
    Stable,
    Beta,
}

/// <summary>
/// Maps the persisted update-channel preference to the effective release line.
/// Automatic keeps a stable build on stable releases and a beta build on beta
/// releases, so a stable install cannot silently move to a prerelease.
/// </summary>
public static class ReleaseChannelPreference
{
    public const string Automatic = "auto";
    public const string Stable = "stable";
    public const string Beta = "beta";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Beta => Beta,
        Stable => Stable,
        _ => Automatic,
    };

    public static ReleaseChannel Resolve(string? preference, bool buildIsBeta) =>
        Normalize(preference) switch
        {
            Beta => ReleaseChannel.Beta,
            Stable => ReleaseChannel.Stable,
            _ => buildIsBeta ? ReleaseChannel.Beta : ReleaseChannel.Stable,
        };

    public static bool IncludesPrereleases(string? preference, bool buildIsBeta) =>
        Resolve(preference, buildIsBeta) == ReleaseChannel.Beta;
}
