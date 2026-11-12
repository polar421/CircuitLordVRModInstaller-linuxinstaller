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
        Assert.True(File.Exists(settingsPath));
    }
}
