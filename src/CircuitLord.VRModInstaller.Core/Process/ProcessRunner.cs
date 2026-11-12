using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Process;

/// <summary>Formats a command for display only. Never used for execution.</summary>
public static class ArgumentListFormatter
{
    public static string Format(string executable, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder(Quote(executable));
        foreach (var a in arguments)
        {
            sb.Append(' ').Append(Quote(a));
        }
        return sb.ToString();
    }

    public static string Quote(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "-_./:=@+".IndexOf(c) >= 0))
        {
            return value;
        }
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}

public sealed class ProcessOutputLine
{
    public required bool IsStandardError { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Runs child processes asynchronously with a real argument list (never a
/// concatenated shell command), streaming output and preserving the raw exit
/// code, timeout and cancellation state.
/// </summary>
public sealed class ProcessRunner
{
    private static readonly TimeSpan MaxCapturedOutput = TimeSpan.FromMinutes(30);

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? timeout = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutLock = new object();
        var timedOut = false;
        var cancelled = false;

        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }
        if (environmentOverrides is not null)
        {
            foreach (var kv in environmentOverrides)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data, false);
        process.ErrorDataReceived += (_, e) => Append(e.Data, true);

        void Append(string? line, bool isError)
        {
            if (line is null)
            {
                return;
            }
            if (isError)
            {
                lock (stdoutLock)
                {
                    if (stderr.Length < 4_000_000)
                    {
                        stderr.AppendLine(line);
                    }
                }
            }
            else
            {
                lock (stdoutLock)
                {
                    if (stdout.Length < 4_000_000)
                    {
                        stdout.AppendLine(line);
                    }
                }
            }
            onOutput?.Invoke(new ProcessOutputLine { IsStandardError = isError, Text = line });
        }

        try
        {
            if (!process.Start())
            {
                return Failed(started, "Process did not start.");
            }
        }
        catch (Exception ex)
        {
            return Failed(started, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t)
        {
            timeoutCts.CancelAfter(t);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
            TrySignal(process);
            try
            {
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The child ignored the request; report the real state rather than pretending it exited.
            }
        }

        // Drain the async readers so no tail of output is lost.
        try
        {
            process.WaitForExit();
        }
        catch
        {
            // Ignore: exit state is reported below when available.
        }

        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            exitCode = -1;
        }

        string outText, errText;
        lock (stdoutLock)
        {
            outText = stdout.ToString();
            errText = stderr.ToString();
        }

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = outText,
            StandardError = errText,
            Duration = DateTimeOffset.Now - started,
            Started = true,
            TimedOut = timedOut,
            Cancelled = cancelled,
        };
    }

    private static void TrySignal(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // SIGTERM, never SIGKILL; children decide how to wind down.
                using var kill = System.Diagnostics.Process.Start(new ProcessStartInfo("kill")
                {
                    ArgumentList = { "-TERM", process.Id.ToString(CultureInfo.InvariantCulture) },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                kill?.WaitForExit(2000);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static ProcessResult Failed(DateTimeOffset started, string reason) => new()
    {
        ExitCode = -1,
        StandardOutput = string.Empty,
        StandardError = string.Empty,
        Duration = DateTimeOffset.Now - started,
        Started = false,
        StartFailure = reason,
    };
}
