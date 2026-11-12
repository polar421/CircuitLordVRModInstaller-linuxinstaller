using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Backend;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Process;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class BackendUpdateTests
{
    [Fact]
    public async Task InvalidRemoteCommitIsRolledBackToThePreviousCheckout()
    {
        using var env = new TempEnv();
        var remote = Path.Combine(env.Root, "remote.git");
        var seed = Path.Combine(env.Root, "seed");
        var checkout = env.Paths.BackendCheckout;

        Git(env.Root, "init", "--bare", remote);
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.name", "Test");
        Git(seed, "config", "user.email", "test@example.invalid");
        WriteRequiredFiles(seed);
        Git(seed, "add", ".");
        Git(seed, "commit", "-m", "valid backend");
        Git(seed, "remote", "add", "origin", "file://" + remote);
        Git(seed, "push", "origin", "main");
        Git(remote, "symbolic-ref", "HEAD", "refs/heads/main");

        var clone = Git(env.Root, "clone", "--depth", "1", "file://" + remote, checkout);
        Assert.Equal(0, clone.ExitCode);
        Git(checkout, "config", "user.name", "Test");
        Git(checkout, "config", "user.email", "test@example.invalid");
        Git(checkout, "remote", "set-url", "origin", BackendManager.RepositoryUrl);
        Git(checkout, "config", "url.file://" + remote + ".insteadOf", BackendManager.RepositoryUrl);

        var previous = Git(checkout, "rev-parse", "HEAD").Output.Trim();

        // The next remote commit is deliberately invalid: install.sh is gone.
        Git(seed, "rm", "install.sh");
        Git(seed, "commit", "-m", "broken backend");
        Git(seed, "push", "origin", "main");

        var manager = new BackendManager(env.Paths, new ProcessRunner());
        var result = await manager.UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(previous, result.PreviousCommit);
        Assert.Equal(previous, result.Installation.CommitHash);
        Assert.Equal(BackendValidationStatus.Validated, result.Installation.ValidationStatus);
        Assert.True(File.Exists(Path.Combine(checkout, "install.sh")));
        Assert.Contains("rolled back", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyCheckoutIsNeverUpdated()
    {
        using var env = new TempEnv();
        var remote = Path.Combine(env.Root, "remote.git");
        var seed = Path.Combine(env.Root, "seed");
        var checkout = env.Paths.BackendCheckout;

        Git(env.Root, "init", "--bare", remote);
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.name", "Test");
        Git(seed, "config", "user.email", "test@example.invalid");
        WriteRequiredFiles(seed);
        Git(seed, "add", ".");
        Git(seed, "commit", "-m", "valid backend");
        Git(seed, "remote", "add", "origin", "file://" + remote);
        Git(seed, "push", "origin", "main");
        Git(remote, "symbolic-ref", "HEAD", "refs/heads/main");
        Git(env.Root, "clone", "--depth", "1", "file://" + remote, checkout);
        Git(checkout, "remote", "set-url", "origin", BackendManager.RepositoryUrl);
        Git(checkout, "config", "url.file://" + remote + ".insteadOf", BackendManager.RepositoryUrl);
        File.WriteAllText(Path.Combine(checkout, "local-notes.txt"), "do not lose this");

        var manager = new BackendManager(env.Paths, new ProcessRunner());
        var result = await manager.UpdateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("uncommitted changes", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(checkout, "local-notes.txt")));
    }

    private static void WriteRequiredFiles(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "install.sh"), "#!/usr/bin/env bash\nexit 0\n");
        File.WriteAllText(Path.Combine(directory, "tf2vr"), "#!/usr/bin/env bash\nexit 0\n");
        File.WriteAllText(Path.Combine(directory, "tf2vr.desktop"), "[Desktop Entry]\n");
    }

    private static GitResult Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "git " + string.Join(' ', arguments) + " failed: " + error);
        }
        return new GitResult(process.ExitCode, output, error);
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
