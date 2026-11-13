using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitLord.VRModInstaller.Core.Steam;

namespace CircuitLord.VRModInstaller.Core.Game;

/// <summary>
/// A read-only Titanfall 2 installation found in a Steam library or in the
/// backend's existing <c>GAME_DIR</c> configuration.
/// </summary>
public sealed record Titanfall2Installation(string GameDirectory, string ExecutablePath, string Source);

/// <summary>
/// Locates the Windows Titanfall 2 installation that Proton will run. The
/// installer backend still owns the real Proton setup; this is only the
/// first-run detection shown before the backend has written its paths file.
/// </summary>
public sealed class TitanfallLocator
{
    public const string SteamAppId = "1237970";
    private const string ExecutableName = "Titanfall2.exe";

    private static readonly Regex LibraryPathPattern = new(
        @"""path""\s*""(?<path>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex InstallDirPattern = new(
        @"""installdir""\s*""(?<path>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SteamLocator _steam;

    public TitanfallLocator(XdgPaths paths, Func<string, string?>? getEnv = null)
    {
        _steam = new SteamLocator(paths, getEnv);
    }

    /// <summary>
    /// Searches the backend's saved game directory first, then every Steam
    /// library. No files are created, modified or deleted.
    /// </summary>
    public Titanfall2Installation? Detect(string? gameDirHint = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (TryFromDirectory(gameDirHint, "backend", seen, out var configured))
        {
            return configured;
        }

        foreach (var installation in _steam.DetectInstallations())
        {
            foreach (var steamApps in SteamAppsDirectories(installation.InstallationRoot))
            {
                if (TryFromSteamApps(steamApps, installation.InstallationType.ToString(), seen, out var found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SteamAppsDirectories(string installationRoot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var rootSteamApps = Path.Combine(installationRoot, "steamapps");
        if (seen.Add(Path.GetFullPath(rootSteamApps)))
        {
            yield return rootSteamApps;
        }

        var libraryFile = Path.Combine(rootSteamApps, "libraryfolders.vdf");
        if (!File.Exists(libraryFile))
        {
            yield break;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(libraryFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (Match match in LibraryPathPattern.Matches(contents))
        {
            var library = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(library))
            {
                continue;
            }

            string fullLibrary;
            try
            {
                fullLibrary = Path.GetFullPath(library);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            var steamApps = Path.Combine(fullLibrary, "steamapps");
            if (seen.Add(steamApps))
            {
                yield return steamApps;
            }
        }
    }

    private static bool TryFromSteamApps(
        string steamApps,
        string source,
        HashSet<string> seen,
        out Titanfall2Installation? installation)
    {
        installation = null;

        // appmanifest is authoritative when Steam has written one. Its
        // installdir may differ from the default folder name.
        var manifest = Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf");
        if (File.Exists(manifest))
        {
            try
            {
                var contents = File.ReadAllText(manifest);
                var match = InstallDirPattern.Match(contents);
                if (match.Success)
                {
                    var candidate = Path.Combine(steamApps, "common", match.Groups["path"].Value);
                    if (TryFromDirectory(candidate, "Steam", seen, out installation))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the conventional folder names.
            }
        }

        foreach (var folder in new[] { "Titanfall2", "Titanfall 2" })
        {
            if (TryFromDirectory(Path.Combine(steamApps, "common", folder), "Steam", seen, out installation))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFromDirectory(
        string? directory,
        string source,
        HashSet<string> seen,
        out Titanfall2Installation? installation)
    {
        installation = null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!seen.Add(full) || !Directory.Exists(full))
        {
            return false;
        }

        var executable = FindExecutable(full);
        if (executable is null)
        {
            return false;
        }

        installation = new Titanfall2Installation(full, executable, source);
        return true;
    }

    private static string? FindExecutable(string directory)
    {
        var exact = Path.Combine(directory, ExecutableName);
        if (File.Exists(exact))
        {
            return exact;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
