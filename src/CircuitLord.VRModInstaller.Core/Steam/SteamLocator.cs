using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Steam;

/// <summary>
/// Discovers Steam installations and local account directories.
///
/// Paths are search hints, not assumptions: every candidate is verified by the
/// presence of a Steam launcher and a config directory before it is offered.
/// Multiple installations and multiple accounts are reported so the user can
/// choose; nothing is modified on the basis of "the first match".
/// </summary>
public sealed class SteamLocator
{
    public const string FlatpakAppId = "com.valvesoftware.Steam";

    private readonly XdgPaths _paths;
    private readonly Func<string, string?> _getEnv;

    public SteamLocator(XdgPaths paths, Func<string, string?>? getEnv = null)
    {
        _paths = paths;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public IReadOnlyList<SteamInstallation> DetectInstallations()
    {
        var candidates = new List<SteamInstallation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string root, SteamInstallationType type, string launcher, string? flatpakId = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }
            var full = Path.GetFullPath(root);
            if (!seen.Add(full))
            {
                return;
            }
            if (!File.Exists(Path.Combine(full, "steam.sh"))
                && !Directory.Exists(Path.Combine(full, "config"))
                && !Directory.Exists(Path.Combine(full, "userdata")))
            {
                return;
            }

            candidates.Add(new SteamInstallation
            {
                InstallationId = type + ":" + full,
                InstallationType = type,
                InstallationRoot = full,
                LauncherCommand = launcher,
                FlatpakAppId = flatpakId,
            });
        }

        var custom = _getEnv("STEAM_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            Add(custom!, SteamInstallationType.Custom, Path.Combine(custom!, "steam.sh"));
        }

        Add(Path.Combine(_paths.Home, ".local/share/Steam"), SteamInstallationType.Native,
            Path.Combine(_paths.Home, ".local/share/Steam/steam.sh"));
        Add(Path.Combine(_paths.Home, ".steam/steam"), SteamInstallationType.Native,
            Path.Combine(_paths.Home, ".steam/steam/steam.sh"));
        Add(Path.Combine(_paths.Home, ".steam/root"), SteamInstallationType.Native,
            Path.Combine(_paths.Home, ".steam/root/steam.sh"));
        Add(Path.Combine(_paths.Home, ".var/app", FlatpakAppId, ".local/share/Steam"),
            SteamInstallationType.Flatpak,
            "flatpak run " + FlatpakAppId,
            FlatpakAppId);

        return candidates;
    }

    public IReadOnlyList<SteamAccount> DetectAccounts(SteamInstallation installation)
    {
        var accounts = new List<SteamAccount>();
        var userdata = Path.Combine(installation.InstallationRoot, "userdata");
        if (!Directory.Exists(userdata))
        {
            return accounts;
        }

        foreach (var directory in Directory.EnumerateDirectories(userdata))
        {
            var accountId = Path.GetFileName(directory);
            if (accountId.Length == 0 || !accountId.All(char.IsDigit))
            {
                continue;
            }
            var shortcuts = Path.Combine(directory, "config", "shortcuts.vdf");
            var configDir = Path.Combine(directory, "config");
            if (!Directory.Exists(configDir))
            {
                continue;
            }
            accounts.Add(new SteamAccount
            {
                AccountDirectory = directory,
                AccountId = accountId,
                ShortcutsFilePath = shortcuts,
                DetectionSource = installation.InstallationType + " userdata",
            });
        }

        return accounts;
    }

    /// <summary>
    /// Picks the account whose shortcuts file was modified most recently, purely
    /// as a preselectable suggestion; the UI always shows the choice.
    /// </summary>
    public SteamAccount? SuggestAccount(IEnumerable<SteamAccount> accounts) =>
        accounts
            .OrderByDescending(a => File.Exists(a.ShortcutsFilePath) ? File.GetLastWriteTimeUtc(a.ShortcutsFilePath) : DateTime.MinValue)
            .FirstOrDefault();
}
