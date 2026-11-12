using System;

namespace CircuitLord.VRModInstaller.Core;

/// <summary>
/// XDG base-directory resolution for the installer.
/// Every directory is derived from the environment with the documented
/// fallbacks from the XDG Base Directory Specification, so nothing is tied to
/// one username or machine.
/// </summary>
public sealed class XdgPaths
{
    public const string AppId = "circuitlord-vrmodinstaller";

    private readonly Func<string, string?> _getEnv;
    private readonly string _home;

    public XdgPaths(Func<string, string?>? getEnv = null, string? home = null)
    {
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _home = home ?? _getEnv("HOME")
            ?? throw new InvalidOperationException("HOME is not set; cannot resolve XDG directories.");
    }

    public string Home => _home;

    public string DataHome => Resolve("XDG_DATA_HOME", ".local/share");
    public string ConfigHome => Resolve("XDG_CONFIG_HOME", ".config");
    public string CacheHome => Resolve("XDG_CACHE_HOME", ".cache");
    public string StateHome => Resolve("XDG_STATE_HOME", ".local/state");

    /// <summary>Application data, including the managed backend checkout.</summary>
    public string AppData => Combine(DataHome, AppId);

    /// <summary>Application configuration (settings, selected token).</summary>
    public string AppConfig => Combine(ConfigHome, AppId);

    /// <summary>Disposable caches (manifest, catalogue, downloaded metadata).</summary>
    public string AppCache => Combine(CacheHome, AppId);

    /// <summary>Logs, operation history and Steam transaction journals.</summary>
    public string AppState => Combine(StateHome, AppId);

    public string ApplicationEntries => Combine(DataHome, "applications");
    public string DesktopEntryFile => Combine(ApplicationEntries, "tf2vr.desktop");

    public string BackendCheckout => Combine(AppData, "backend");
    public string LogsDirectory => Combine(AppState, "logs");
    public string HistoryFile => Combine(AppState, "history.json");
    public string SteamJournalDirectory => Combine(AppState, "steam-transactions");
    public string CatalogueCacheFile => Combine(AppCache, "catalogue.json");
    public string ManifestCacheFile => Combine(AppCache, "manifest-v3.json");
    public string SettingsFile => Combine(AppConfig, "settings.json");
    public string SteamStateFile => Combine(AppConfig, "steam.json");

    /// <summary>
    /// Resolves the backend's own installed launcher (<c>tf2vr</c>). The backend
    /// installs it to <c>~/.local/bin/tf2vr</c>; that path is fixed by
    /// install.sh, not configurable through XDG.
    /// </summary>
    public string InstalledLauncher => Combine(Home, ".local/bin/tf2vr");

    /// <summary>Backend state directory written by install.sh (selection, version).</summary>
    public string BackendState => Combine(DataHome, "tf2vr");

    public string BackendSelectionFile => Combine(BackendState, "selection");
    public string BackendVersionFile => Combine(BackendState, "version");
    public string BackendPathsFile => Combine(ConfigHome, "tf2vr/paths.env");

    private string Resolve(string variable, string fallbackRelative)
    {
        var value = _getEnv(variable);
        return !string.IsNullOrWhiteSpace(value) ? value : Combine(_home, fallbackRelative);
    }

    public static string Combine(string a, string b) => System.IO.Path.Combine(a, b);
}
