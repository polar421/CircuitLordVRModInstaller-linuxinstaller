using System;
using System.IO;
using CircuitLord.VRModInstaller.Core.Settings;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void PreferencesRoundTripThroughTheSettingsFile()
    {
        using var env = new TempEnv();
        var store = new SettingsStore(env.Paths);
        var settings = new AppSettings
        {
            RoundedCorners = false,
            Language = "de",
            Theme = "light",
            UiScale = 125,
            MotionPreference = "reduced",
            CheckForAppUpdates = false,
            UpdateBackendAutomatically = true,
            ConfirmSteamRestart = false,
            LastBackendUpdateAt = DateTimeOffset.Parse("2026-11-12T01:00:00+00:00"),
            LastBackendUpdateResult = "Updated to abc1234",
            LastSteamIntegrationAt = DateTimeOffset.Parse("2026-11-12T02:00:00+00:00"),
            LastSteamIntegrationResult = "Shortcut installed",
        };
        store.Save(settings);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.RoundedCorners);
        Assert.Equal("de", loaded.Language);
        Assert.Equal("light", loaded.Theme);
        Assert.Equal(125, loaded.UiScale);
        Assert.Equal("reduced", loaded.MotionPreference);
        Assert.False(loaded.CheckForAppUpdates);
        Assert.True(loaded.UpdateBackendAutomatically);
        Assert.False(loaded.ConfirmSteamRestart);
        Assert.Equal(settings.LastBackendUpdateAt, loaded.LastBackendUpdateAt);
        Assert.Equal("Updated to abc1234", loaded.LastBackendUpdateResult);
        Assert.Equal(settings.LastSteamIntegrationAt, loaded.LastSteamIntegrationAt);
        Assert.Equal("Shortcut installed", loaded.LastSteamIntegrationResult);
    }

    [Fact]
    public void SchemaTwoMigratesWithSteamConfirmationEnabled()
    {
        using var env = new TempEnv();
        var settingsPath = env.Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath,
            """
            {
              "schemaVersion": 2,
              "selectedToken": null,
              "roundedCorners": false,
              "language": "de",
              "checkForAppUpdates": false,
              "updateBackendAutomatically": true
            }
            """);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.RoundedCorners);
        Assert.Equal("de", loaded.Language);
        Assert.True(loaded.ConfirmSteamRestart);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(100, loaded.UiScale);
        Assert.Equal("system", loaded.MotionPreference);
        Assert.True(File.Exists(settingsPath));
    }

    [Fact]
    public void SchemaThreeMigrationPreservesExistingPreferencesAndAddsAppearanceDefaults()
    {
        using var env = new TempEnv();
        var settingsPath = env.Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath,
            """
            {
              "schemaVersion": 3,
              "selectedToken": "beta",
              "roundedCorners": false,
              "language": "de",
              "checkForAppUpdates": false,
              "updateBackendAutomatically": true,
              "confirmSteamRestart": false,
              "showLaunchConfirmation": false
            }
            """);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("beta", loaded.SelectedToken);
        Assert.False(loaded.RoundedCorners);
        Assert.Equal("de", loaded.Language);
        Assert.True(loaded.UpdateBackendAutomatically);
        Assert.False(loaded.ConfirmSteamRestart);
        Assert.False(loaded.ShowLaunchConfirmation);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(100, loaded.UiScale);
        Assert.Equal("system", loaded.MotionPreference);
        Assert.NotEmpty(Directory.GetFiles(env.Paths.AppConfig, "settings.json.backup-*"));
    }

    [Fact]
    public void InvalidAppearanceValuesAreNormalizedToSafeDefaults()
    {
        using var env = new TempEnv();
        var settingsPath = env.Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath,
            """
            {
              "schemaVersion": 4,
              "theme": "neon",
              "uiScale": 137,
              "motionPreference": "sometimes"
            }
            """);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(135, loaded.UiScale);
        Assert.Equal("system", loaded.MotionPreference);
    }
}
