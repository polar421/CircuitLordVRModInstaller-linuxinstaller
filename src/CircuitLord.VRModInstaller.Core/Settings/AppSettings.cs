using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitLord.VRModInstaller.Core.Settings;

/// <summary>
/// Versioned local settings stored in $XDG_CONFIG_HOME. The selected release is
/// persisted as the exact backend token, never as a dropdown ordinal.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("selectedToken")]
    public string? SelectedToken { get; set; }

    [JsonPropertyName("autoRefreshCatalogue")]
    public bool AutoRefreshCatalogue { get; set; } = true;

    [JsonPropertyName("updateBackendAutomatically")]
    public bool UpdateBackendAutomatically { get; set; }

    [JsonPropertyName("logRetentionDays")]
    public int LogRetentionDays { get; set; } = 30;

    [JsonPropertyName("selectedSteamInstallationId")]
    public string? SelectedSteamInstallationId { get; set; }

    [JsonPropertyName("selectedSteamAccountId")]
    public string? SelectedSteamAccountId { get; set; }

    [JsonPropertyName("addSteamShortcutAfterInstall")]
    public bool AddSteamShortcutAfterInstall { get; set; }

    [JsonPropertyName("showLaunchConfirmation")]
    public bool ShowLaunchConfirmation { get; set; } = true;
}

public sealed class SettingsStore
{
    private readonly XdgPaths _paths;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public SettingsStore(XdgPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_paths.SettingsFile))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_paths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();

            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // Never silently reset settings from a newer build.
                throw new InvalidDataException(
                    $"Settings schema {settings.SchemaVersion} is newer than this build supports.");
            }

            if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
            {
                File.Copy(_paths.SettingsFile, _paths.SettingsFile + ".backup-" + DateTimeOffset.Now.ToString("yyyyMMddHHmmss"), overwrite: false);
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_paths.AppConfig);
        var temp = _paths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, _options));
        File.Move(temp, _paths.SettingsFile, overwrite: true);
    }
}
