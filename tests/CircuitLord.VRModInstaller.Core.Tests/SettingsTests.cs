using System;
using System.IO;
using CircuitLord.VRModInstaller.Core.Appearance;
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
            LayoutDensity = AppSettings.CompactDensity,
            DetailsExpanded = true,
            UiFontFamily = "system:Noto Serif",
            TechnicalFontFamily = "system:DejaVu Sans Mono",
            UpdateChannel = "beta",
            CheckForAppUpdates = false,
            UpdateBackendAutomatically = true,
            ConfirmSteamRestart = false,
            LastBackendUpdateAt = DateTimeOffset.Parse("2026-11-12T01:00:00+00:00"),
            LastBackendUpdateResult = "Updated to abc1234",
            LastSteamIntegrationAt = DateTimeOffset.Parse("2026-11-12T02:00:00+00:00"),
            LastSteamIntegrationResult = "Shortcut installed",
            SetupWizardCompleted = true,
        };
        store.Save(settings);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.RoundedCorners);
        Assert.Equal("de", loaded.Language);
        Assert.Equal("light", loaded.Theme);
        Assert.Equal(125, loaded.UiScale);
        Assert.Equal("reduced", loaded.MotionPreference);
        Assert.Equal(AppSettings.CompactDensity, loaded.LayoutDensity);
        Assert.True(loaded.DetailsExpanded);
        Assert.Equal("system:Noto Serif", loaded.UiFontFamily);
        Assert.Equal("system:DejaVu Sans Mono", loaded.TechnicalFontFamily);
        Assert.Equal("beta", loaded.UpdateChannel);
        Assert.False(loaded.CheckForAppUpdates);
        Assert.True(loaded.UpdateBackendAutomatically);
        Assert.False(loaded.ConfirmSteamRestart);
        Assert.Equal(settings.LastBackendUpdateAt, loaded.LastBackendUpdateAt);
        Assert.Equal("Updated to abc1234", loaded.LastBackendUpdateResult);
        Assert.Equal(settings.LastSteamIntegrationAt, loaded.LastSteamIntegrationAt);
        Assert.Equal("Shortcut installed", loaded.LastSteamIntegrationResult);
        Assert.True(loaded.SetupWizardCompleted);
    }

    [Fact]
    public void SchemaSixMigrationKeepsTheSetupWizardForFirstRun()
    {
        using var env = new TempEnv();
        var settingsPath = env.Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath,
            """
            {
              "schemaVersion": 6,
              "language": "de",
              "selectedToken": "beta"
            }
            """);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.SetupWizardCompleted);
        Assert.Equal("de", loaded.Language);
        Assert.Equal("beta", loaded.SelectedToken);
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
        Assert.Equal("auto", loaded.UpdateChannel);
        Assert.Equal(FontPreference.DefaultUiFontId, loaded.UiFontFamily);
        Assert.Equal(FontPreference.FollowInterfaceFontId, loaded.TechnicalFontFamily);
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
        Assert.Equal("auto", loaded.UpdateChannel);
        Assert.Equal(FontPreference.DefaultUiFontId, loaded.UiFontFamily);
        Assert.Equal(FontPreference.FollowInterfaceFontId, loaded.TechnicalFontFamily);
        Assert.NotEmpty(Directory.GetFiles(env.Paths.AppConfig, "settings.json.backup-*"));
    }

    [Fact]
    public void SchemaFiveMigrationPreservesSavedFontPreferences()
    {
        using var env = new TempEnv();
        var settingsPath = env.Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath,
            """
            {
              "schemaVersion": 5,
              "language": "en",
              "uiFontFamily": "system:Noto Serif",
              "technicalFontFamily": "system:JetBrains Mono"
            }
            """);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("system:Noto Serif", loaded.UiFontFamily);
        Assert.Equal("system:JetBrains Mono", loaded.TechnicalFontFamily);
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
              "motionPreference": "sometimes",
              "layoutDensity": "dense",
              "uiFontFamily": "not-a-font",
              "technicalFontFamily": "also-not-a-font",
              "updateChannel": "nightly"
            }
            """);

        var loaded = new SettingsStore(env.Paths).Load();

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(135, loaded.UiScale);
        Assert.Equal("system", loaded.MotionPreference);
        Assert.Equal(AppSettings.ComfortableDensity, loaded.LayoutDensity);
        Assert.False(loaded.DetailsExpanded);
        Assert.Equal(FontPreference.DefaultUiFontId, loaded.UiFontFamily);
        Assert.Equal(FontPreference.FollowInterfaceFontId, loaded.TechnicalFontFamily);
        Assert.Equal("auto", loaded.UpdateChannel);
    }
}
