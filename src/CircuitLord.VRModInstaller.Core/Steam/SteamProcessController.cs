using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Steam;

/// <summary>
/// Observable Steam client state and the two actions the transaction needs:
/// a graceful shutdown request and relaunching the same installation.
///
/// Deliberately conservative: a process counts as the Steam client only when its
/// command line references this installation (or its Flatpak id). Force
/// termination is never used; if graceful shutdown does not complete, the
/// transaction stops without touching shortcut data.
/// </summary>
public interface ISteamProcessController
{
    bool IsClientRunning(SteamInstallation installation);
    Task RequestGracefulShutdownAsync(SteamInstallation installation, CancellationToken cancellationToken);
    Task<bool> WaitForExitAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken);
    Task<bool> LaunchAsync(SteamInstallation installation, CancellationToken cancellationToken);
    Task<bool> WaitForStartupAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken);
    bool IsGameSessionRunning();
}

public sealed class SteamProcessController : ISteamProcessController
{
    private readonly Func<int, string?> _readCommandLine;

    public SteamProcessController(Func<int, string?>? readCommandLine = null)
    {
        _readCommandLine = readCommandLine ?? ReadProcCommandLine;
    }

    public bool IsClientRunning(SteamInstallation installation) =>
        EnumerateCommandLines().Any(cmd => MatchesClient(cmd, installation));

    public async Task RequestGracefulShutdownAsync(SteamInstallation installation, CancellationToken cancellationToken)
    {
        // Verified per installation type: the Steam launcher accepts -shutdown,
        // which asks the client to close normally and save its state.
        var (file, args) = LaunchTarget(installation, "-shutdown");
        await StartAsync(file, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WaitForExitAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsClientRunning(installation))
            {
                return true;
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return !IsClientRunning(installation);
    }

    public async Task<bool> LaunchAsync(SteamInstallation installation, CancellationToken cancellationToken)
    {
        if (IsClientRunning(installation))
        {
            return true;
        }
        var (file, args) = LaunchTarget(installation);
        return await StartAsync(file, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WaitForStartupAsync(SteamInstallation installation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsClientRunning(installation))
            {
                return true;
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return IsClientRunning(installation);
    }

    /// <summary>
    /// Best-effort detection of a running game/VR session. Used only to warn the
    /// user; it never terminates anything and is reported with low confidence.
    /// </summary>
    public bool IsGameSessionRunning() =>
        EnumerateCommandLines().Any(cmd =>
            cmd.Contains("Titanfall2.exe", StringComparison.OrdinalIgnoreCase)
            || cmd.Contains("Titanfall2VRLauncher.exe", StringComparison.OrdinalIgnoreCase)
            || cmd.Contains("Titanfall2VR", StringComparison.OrdinalIgnoreCase));

    private static bool MatchesClient(string commandLine, SteamInstallation installation)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        if (installation.InstallationType == SteamInstallationType.Flatpak)
        {
            return commandLine.Contains(SteamLocator.FlatpakAppId, StringComparison.Ordinal);
        }

        var root = installation.InstallationRoot;
        return commandLine.Contains(root, StringComparison.Ordinal)
            && (commandLine.Contains("steam.sh", StringComparison.Ordinal)
                || commandLine.Contains("/steam ", StringComparison.Ordinal)
                || commandLine.Contains("/steam\0", StringComparison.Ordinal)
                || commandLine.Contains("ubuntu12_32/steam", StringComparison.Ordinal));
    }

    private static (string File, IReadOnlyList<string> Args) LaunchTarget(SteamInstallation installation, string? extra = null)
    {
        if (installation.InstallationType == SteamInstallationType.Flatpak)
        {
            var args = new List<string> { "run", SteamLocator.FlatpakAppId };
            if (extra is not null)
            {
                args.Add(extra);
            }
            return ("flatpak", args);
        }

        var script = Path.Combine(installation.InstallationRoot, "steam.sh");
        var target = File.Exists(script) ? script : installation.LauncherCommand;
        return (target, extra is null ? Array.Empty<string>() : new[] { extra });
    }

    private static async Task<bool> StartAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var process = System.Diagnostics.Process.Start(psi);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private IEnumerable<string> EnumerateCommandLines()
    {
        if (!Directory.Exists("/proc"))
        {
            yield break;
        }

        var self = Environment.ProcessId;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, out var pid) || pid == self)
            {
                continue;
            }
            var commandLine = _readCommandLine(pid);
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                yield return commandLine;
            }
        }
    }

    private static string? ReadProcCommandLine(int pid)
    {
        try
        {
            var bytes = File.ReadAllBytes("/proc/" + pid + "/cmdline");
            if (bytes.Length == 0)
            {
                return null;
            }
            return System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
