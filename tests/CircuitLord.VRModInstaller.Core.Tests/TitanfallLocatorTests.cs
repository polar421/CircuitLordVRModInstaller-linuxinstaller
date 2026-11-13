using System;
using System.IO;
using CircuitLord.VRModInstaller.Core.Game;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class TitanfallLocatorTests
{
    [Fact]
    public void FindsTitanfallInTheDefaultSteamLibrary()
    {
        using var env = new TempEnv();
        var game = CreateGame(SteamRoot(env), "Titanfall2");

        var installation = new TitanfallLocator(env.Paths).Detect();

        Assert.NotNull(installation);
        Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(installation!.GameDirectory));
        Assert.Equal("Steam", installation.Source);
        Assert.True(File.Exists(installation.ExecutablePath));
    }

    [Fact]
    public void ReadsAdditionalSteamLibrariesFromLibraryFolders()
    {
        using var env = new TempEnv();
        var steamRoot = SteamRoot(env);
        Directory.CreateDirectory(Path.Combine(steamRoot, "config"));
        var secondLibrary = Path.Combine(env.Root, "games");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        File.WriteAllText(
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            """
            "libraryfolders"
            {
                "0"
                {
                    "path" "/tmp/not-used"
                }
                "1"
                {
                    "path" "SECOND_LIBRARY"
                }
            }
            """.Replace("SECOND_LIBRARY", secondLibrary));
        var game = CreateGame(secondLibrary, "Titanfall2");

        var installation = new TitanfallLocator(env.Paths).Detect();

        Assert.NotNull(installation);
        Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(installation!.GameDirectory));
    }

    [Fact]
    public void HonorsTheBackendGameDirectoryBeforeScanningSteam()
    {
        using var env = new TempEnv();
        var configured = Path.Combine(env.Root, "custom", "Titanfall2");
        Directory.CreateDirectory(configured);
        File.WriteAllText(Path.Combine(configured, "Titanfall2.exe"), "fixture");

        var installation = new TitanfallLocator(env.Paths).Detect(configured);

        Assert.NotNull(installation);
        Assert.Equal("backend", installation!.Source);
        Assert.Equal(configured, installation.GameDirectory);
    }

    [Fact]
    public void UsesTheManifestInstalldirWhenTheFolderHasBeenRenamed()
    {
        using var env = new TempEnv();
        var steamRoot = SteamRoot(env);
        var steamApps = Path.Combine(steamRoot, "steamapps");
        var game = Path.Combine(steamApps, "common", "Titanfall 2 — VR");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Titanfall2.exe"), "fixture");
        File.WriteAllText(
            Path.Combine(steamApps, "appmanifest_1237970.acf"),
            """
            "AppState"
            {
                "appid" "1237970"
                "installdir" "Titanfall 2 — VR"
            }
            """);

        var installation = new TitanfallLocator(env.Paths).Detect();

        Assert.NotNull(installation);
        Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(installation!.GameDirectory));
    }

    [Fact]
    public void DoesNotClaimAFolderWithoutTheGameExecutable()
    {
        using var env = new TempEnv();
        var folder = CreateGame(SteamRoot(env), "Titanfall2");
        File.Delete(Path.Combine(folder, "Titanfall2.exe"));

        Assert.Null(new TitanfallLocator(env.Paths).Detect());
    }

    [Fact]
    public void MissingSteamAndMissingHintReturnNull()
    {
        using var env = new TempEnv();

        Assert.Null(new TitanfallLocator(env.Paths).Detect(Path.Combine(env.Root, "absent")));
    }

    private static string SteamRoot(TempEnv env)
    {
        var root = Path.Combine(env.Paths.Home, ".local", "share", "Steam");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static string CreateGame(string steamRoot, string folder)
    {
        var game = Path.Combine(steamRoot, "steamapps", "common", folder);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Titanfall2.exe"), "fixture");
        return game;
    }
}
