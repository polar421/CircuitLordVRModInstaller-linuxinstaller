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
    public const int CurrentSchemaVersion = 4;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("selectedToken")]
    public string? SelectedToken { get; set; }

    [JsonPropertyName("autoRefreshCatalogue")]
    public bool AutoRefreshCatalogue { get; set; } = true;

    [JsonPropertyName("roundedCorners")]
    public bool RoundedCorners { get; set; } = true;

    /// <summary>Interface language; validated against the bundled translation catalog.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>Dark, Light or System. Dark remains the default visual identity.</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    /// <summary>Application interface scale from 80 to 150 percent, in five-percent steps.</summary>
    [JsonPropertyName("uiScale")]
    public int UiScale { get; set; } = 100;

    /// <summary>System, Reduced or Full motion preference.</summary>
    [JsonPropertyName("motionPreference")]
    public string MotionPreference { get; set; } = "system";

    [JsonPropertyName("checkForAppUpdates")]
    public bool CheckForAppUpdates { get; set; } = true;

    [JsonPropertyName("updateBackendAutomatically")]
    public bool UpdateBackendAutomatically { get; set; }

    /// <summary>Ask before a shortcut operation restarts a running Steam client.</summary>
    [JsonPropertyName("confirmSteamRestart")]
    public bool ConfirmSteamRestart { get; set; } = true;

    [JsonPropertyName("lastBackendUpdateAt")]
    public DateTimeOffset? LastBackendUpdateAt { get; set; }

    [JsonPropertyName("lastBackendUpdateResult")]
    public string? LastBackendUpdateResult { get; set; }

    [JsonPropertyName("lastSteamIntegrationAt")]
    public DateTimeOffset? LastSteamIntegrationAt { get; set; }

    [JsonPropertyName("lastSteamIntegrationResult")]
    public string? LastSteamIntegrationResult { get; set; }

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

    /// <summary>
    /// Keeps older and hand-edited settings safe. Unknown values fall back to
    /// the documented defaults rather than escaping into the UI layer.
    /// </summary>
    public void Normalize()
    {
        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "light" => "light",
            _ => "dark",
        };
        MotionPreference = MotionPreference?.Trim().ToLowerInvariant() switch
        {
            "reduced" => "reduced",
            "full" => "full",
            _ => "system",
        };
        UiScale = Math.Clamp(UiScale, 80, 150);
        UiScale = (int)(Math.Round(UiScale / 5.0, MidpointRounding.AwayFromZero) * 5);
        if (string.IsNullOrWhiteSpace(Language))
        {
            Language = "en";
        }
    }
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
                var defaults = new AppSettings();
                defaults.Normalize();
                return defaults;
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
                try
                {
                    // Persist the migration so the backup is not repeated on every start.
                    Save(settings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep using the migrated in-memory settings even if the file is read-only.
                }
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            var defaults = new AppSettings();
            defaults.Normalize();
            return defaults;
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
