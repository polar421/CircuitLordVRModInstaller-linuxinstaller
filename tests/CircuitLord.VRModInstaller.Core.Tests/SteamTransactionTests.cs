using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Steam;

namespace CircuitLord.VRModInstaller.Core.Tests;

public sealed class TempEnv : IDisposable
{
    public TempEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "clvrmi-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Paths = new XdgPaths(
            name => Path.Combine(Root, name),
            Path.Combine(Root, "home"));
        Directory.CreateDirectory(Paths.AppConfig);
        Directory.CreateDirectory(Paths.AppState);
        Directory.CreateDirectory(Paths.ApplicationEntries);
    }

    public string Root { get; }
    public XdgPaths Paths { get; }

    public string Launcher
    {
        get
        {
            // The backend installs the launcher at ~/.local/bin/tf2vr; the
            // fixture must use the same path the manager resolves.
            var path = Paths.InstalledLauncher;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return path;
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class FakeSteam : ISteamProcessController
{
    public bool Running { get; set; }
    public bool RefuseToExit { get; set; }
    public bool FailLaunch { get; set; }
    public int ShutdownRequests { get; private set; }
    public int LaunchRequests { get; private set; }

    public bool IsClientRunning(SteamInstallation installation) => Running;

    public Task RequestGracefulShutdownAsync(SteamInstallation installation, CancellationToken cancellationToken)
    {
        ShutdownRequests++;
        if (!RefuseToExit)
        {
            Running = false;
        }
        return Task.CompletedTask;
    }

    public Task<bool> WaitForExitAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(!Running);

    public Task<bool> LaunchAsync(SteamInstallation installation, CancellationToken cancellationToken)
    {
        LaunchRequests++;
        if (FailLaunch)
        {
            return Task.FromResult(false);
        }
        Running = true;
        return Task.FromResult(true);
    }

    public Task<bool> WaitForStartupAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(Running);

    public bool IsGameSessionRunning() => false;
}

public class SteamTransactionTests
{
    private static SteamInstallation Installation = new()
    {
        InstallationId = "native:/tmp/steam",
        InstallationType = SteamInstallationType.Native,
        InstallationRoot = "/tmp/steam",
        LauncherCommand = "/tmp/steam/steam.sh",
    };

    private static SteamAccount Account(TempEnv env, out string shortcutsPath)
    {
        var config = Path.Combine(env.Root, "steam", "userdata", "12345", "config");
        Directory.CreateDirectory(config);
        shortcutsPath = Path.Combine(config, "shortcuts.vdf");
        return new SteamAccount
        {
            AccountDirectory = Path.GetDirectoryName(config)!,
            AccountId = "12345",
            ShortcutsFilePath = shortcutsPath,
        };
    }

    private static void SeedShortcut(string path, string name, string exe)
    {
        var document = SteamShortcutsDocument.Empty();
        document.Upsert(new SteamShortcutRecord { Name = name, Executable = exe, StartDirectory = "/tmp" });
        document.SaveVerified(path);
    }

    [Fact]
    public void VdfRoundTripPreservesUnrelatedShortcutsAndUnknownFields()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        SeedShortcut(path, "Other Game", "/usr/bin/other");

        // Inject an unknown key the way a future Steam build might.
        var bytes = BinaryVdf.Parse(File.ReadAllBytes(path));
        ((VdfMap)((VdfMap)bytes["shortcuts"]!)["0"]!)["UnknownFutureField"] = new VdfString("keep me");
        File.WriteAllBytes(path, BinaryVdf.Serialize(bytes));

        var document = SteamShortcutsDocument.Load(path);
        document.Upsert(new SteamShortcutRecord { Name = "Titanfall 2 VR", Executable = env.Launcher, StartDirectory = "/tmp" });
        document.SaveVerified(path);

        var reloaded = SteamShortcutsDocument.Load(path);
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Contains(reloaded.Entries, e => e.Name == "Other Game" && e.Executable == "/usr/bin/other");

        var map = BinaryVdf.Parse(File.ReadAllBytes(path)).GetMap("shortcuts")!.GetMap("0")!;
        Assert.Equal("keep me", map.GetString("UnknownFutureField"));
    }

    [Fact]
    public void UpsertIsIdempotentAndNeverDuplicates()
    {
        using var env = new TempEnv();
        Account(env, out var path);
        var launcher = env.Launcher;

        var document = SteamShortcutsDocument.Load(path);
        document.Upsert(new SteamShortcutRecord { Name = "Titanfall 2 VR", Executable = launcher });
        document.SaveVerified(path);
        SteamShortcutsDocument.Load(path).Upsert(new SteamShortcutRecord { Name = "Titanfall 2 VR", Executable = launcher });
        SteamShortcutsDocument.Load(path).SaveVerified(path);

        var final = SteamShortcutsDocument.Load(path);
        Assert.Single(final.Entries, e => e.Name == "Titanfall 2 VR");
    }

    [Fact]
    public void ConflictingEntryWithTheSameNameIsNotOverwritten()
    {
        using var env = new TempEnv();
        Account(env, out var path);
        SeedShortcut(path, "Titanfall 2 VR", "/somebody/elses/launcher");

        var document = SteamShortcutsDocument.Load(path);
        var match = document.Match("Titanfall 2 VR", env.Launcher, out _);

        Assert.Equal(ShortcutMatch.Ambiguous, match);
        Assert.Throws<InvalidOperationException>(() =>
            document.Upsert(new SteamShortcutRecord { Name = "Titanfall 2 VR", Executable = env.Launcher }));
    }

    [Fact]
    public async Task SteamRunningIsClosedWrittenAndReopened()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        SeedShortcut(path, "Other Game", "/usr/bin/other");
        var steam = new FakeSteam { Running = true };
        var manager = new SteamShortcutManager(env.Paths, steam);

        var result = await manager.AddOrUpdateAsync(Installation, account, env.Launcher, allowSteamRestart: true);

        Assert.Equal(ShortcutOutcome.Added, result.Shortcut);
        Assert.Equal(RestartOutcome.Verified, result.Restart);
        Assert.Equal(1, steam.ShutdownRequests);
        Assert.True(steam.Running);
        Assert.True(result.Transaction.BackupPath is not null && File.Exists(result.Transaction.BackupPath));

        var shortcuts = SteamShortcutsDocument.Load(path);
        Assert.Contains(shortcuts.Entries, e => e.Name == "Other Game");
        Assert.Single(shortcuts.Entries, e => e.Name == "Titanfall 2 VR");
    }

    [Fact]
    public async Task RefusingToRestartDoesNothingWhenSteamWasRunning()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        var steam = new FakeSteam { Running = true };
        var manager = new SteamShortcutManager(env.Paths, steam);

        var result = await manager.AddOrUpdateAsync(Installation, account, env.Launcher, allowSteamRestart: false);

        Assert.Equal(ShortcutOutcome.NotAttempted, result.Shortcut);
        Assert.Equal(0, steam.ShutdownRequests);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ShutdownTimeoutWritesNothingAndNeverForceKills()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        SeedShortcut(path, "Other Game", "/usr/bin/other");
        var before = File.ReadAllBytes(path);
        var steam = new FakeSteam { Running = true, RefuseToExit = true };
        var manager = new SteamShortcutManager(env.Paths, steam);

        var result = await manager.AddOrUpdateAsync(Installation, account, env.Launcher, allowSteamRestart: true);

        Assert.Equal(ShortcutOutcome.NotAttempted, result.Shortcut);
        Assert.Equal(SteamTransactionState.ShutdownTimedOut, result.Transaction.State);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(0, steam.LaunchRequests);
        Assert.True(steam.Running);
    }

    [Fact]
    public async Task ShortcutWriteFailureStillReopensSteam()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        SeedShortcut(path, "Other Game", "/usr/bin/other");
        var before = File.ReadAllBytes(path);

        var configDir = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(configDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var steam = new FakeSteam { Running = true };
        var manager = new SteamShortcutManager(env.Paths, steam);

        SteamTransactionResult result;
        try
        {
            result = await manager.AddOrUpdateAsync(Installation, account, env.Launcher, allowSteamRestart: true);
        }
        finally
        {
            File.SetUnixFileMode(configDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(ShortcutOutcome.Failed, result.Shortcut);
        Assert.Equal(RestartOutcome.Verified, result.Restart);
        Assert.True(steam.Running);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(SteamTransactionState.SteamRestartedWithIntegrationFailure, result.Transaction.State);
    }

    [Fact]
    public async Task SteamFailingToReopenIsReportedAsRecoveryRequired()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        var steam = new FakeSteam { Running = true, FailLaunch = true };
        var manager = new SteamShortcutManager(env.Paths, steam);

        var result = await manager.AddOrUpdateAsync(Installation, account, env.Launcher, allowSteamRestart: true);

        Assert.Equal(ShortcutOutcome.Added, result.Shortcut);
        Assert.Equal(RestartOutcome.Failed, result.Restart);
        Assert.True(result.Transaction.RecoveryRequired);
        Assert.Equal(SteamTransactionState.SteamRestartFailedNeedsRecovery, result.Transaction.State);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SteamThatWasClosedStaysClosed()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        var steam = new FakeSteam { Running = false };
        var manager = new SteamShortcutManager(env.Paths, steam);

        var result = await manager.AddOrUpdateAsync(Installation, account, env.Launcher, allowSteamRestart: true);

        Assert.Equal(ShortcutOutcome.Added, result.Shortcut);
        Assert.Equal(RestartOutcome.NotNeeded, result.Restart);
        Assert.Equal(0, steam.ShutdownRequests);
        Assert.Equal(0, steam.LaunchRequests);
        Assert.False(steam.Running);
    }

    [Fact]
    public async Task RemovalOnlyDeletesTheOwnedEntry()
    {
        using var env = new TempEnv();
        var account = Account(env, out var path);
        SeedShortcut(path, "Other Game", "/usr/bin/other");
        var document = SteamShortcutsDocument.Load(path);
        document.Upsert(new SteamShortcutRecord { Name = "Titanfall 2 VR", Executable = env.Launcher });
        document.SaveVerified(path);

        var steam = new FakeSteam { Running = false };
        var manager = new SteamShortcutManager(env.Paths, steam);
        var result = await manager.RemoveAsync(Installation, account, env.Launcher, allowSteamRestart: true);

        Assert.Equal(ShortcutOutcome.Removed, result.Shortcut);
        var remaining = SteamShortcutsDocument.Load(path);
        Assert.Single(remaining.Entries);
        Assert.Equal("Other Game", remaining.Entries[0].Name);
    }
}
