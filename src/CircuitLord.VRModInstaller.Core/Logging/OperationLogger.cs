using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Process;

namespace CircuitLord.VRModInstaller.Core.Logging;

/// <summary>
/// Persistent operation logs under $XDG_STATE_HOME plus the installation
/// history. Logs never contain credentials; only local paths, commands and
/// captured process output.
/// </summary>
public sealed class OperationLogger
{
    private readonly XdgPaths _paths;
    private readonly object _gate = new();

    public OperationLogger(XdgPaths paths)
    {
        _paths = paths;
    }

    public string CreateOperationLog(string operationId, BackendCommand command, string? backendCommit)
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        var path = Path.Combine(_paths.LogsDirectory, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + operationId + ".log");
        var header = new StringBuilder()
            .AppendLine("CircuitLord's VR Mod Installer (Linux)")
            .AppendLine("Operation:   " + command.OperationKind)
            .AppendLine("Operation ID:" + operationId)
            .AppendLine("Started:     " + DateTimeOffset.Now.ToString("O"))
            .AppendLine("Command:     " + command.QuotedCommandLine)
            .AppendLine("Working dir: " + command.WorkingDirectory)
            .AppendLine("Backend:     " + (backendCommit ?? "unknown"))
            .AppendLine(new string('-', 72));
        File.WriteAllText(path, header.ToString());
        return path;
    }

    public void Append(string logPath, ProcessOutputLine line)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(logPath, (line.IsStandardError ? "[stderr] " : string.Empty) + line.Text + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging failures must not abort an installation.
            }
        }
    }

    public void Complete(string logPath, ProcessResult result, string? postCheck)
    {
        lock (_gate)
        {
            try
            {
                var summary = new StringBuilder()
                    .AppendLine(new string('-', 72))
                    .AppendLine("Exit code:  " + result.ExitCode)
                    .AppendLine("Duration:   " + result.Duration)
                    .AppendLine("Timed out:  " + result.TimedOut)
                    .AppendLine("Cancelled:  " + result.Cancelled)
                    .AppendLine("Result:     " + (result.Succeeded ? "success" : "failure"));
                if (postCheck is not null)
                {
                    summary.AppendLine("Post-check: " + postCheck);
                }
                File.AppendAllText(logPath, summary.ToString());
            }
            catch (IOException)
            {
            }
        }
    }

    public void RecordOperation(InstallerOperationRecord record)
    {
        try
        {
            Directory.CreateDirectory(_paths.AppState);
            var history = ReadHistory().ToList();
            history.Insert(0, record);
            if (history.Count > 100)
            {
                history = history.Take(100).ToList();
            }
            var temp = _paths.HistoryFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _paths.HistoryFile, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    public IReadOnlyList<InstallerOperationRecord> ReadHistory()
    {
        try
        {
            if (!File.Exists(_paths.HistoryFile))
            {
                return Array.Empty<InstallerOperationRecord>();
            }
            var records = JsonSerializer.Deserialize<List<InstallerOperationRecord>>(File.ReadAllText(_paths.HistoryFile));
            return records ?? new List<InstallerOperationRecord>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return Array.Empty<InstallerOperationRecord>();
        }
    }
}
