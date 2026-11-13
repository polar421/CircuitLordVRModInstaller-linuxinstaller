using System;
using System.IO;
using CircuitLord.VRModInstaller.Core.Logging;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class OperationHistoryTests
{
    [Fact]
    public void HistoryRoundTripsOperationDetailsUsedByTheReport()
    {
        using var env = new TempEnv();
        var logger = new OperationLogger(env.Paths);
        var started = DateTimeOffset.Parse("2026-11-12T09:00:00+00:00");
        logger.RecordOperation(new InstallerOperationRecord
        {
            OperationId = "operation-1",
            Kind = BackendOperationKind.Repair,
            RequestedToken = "beta",
            RequestedDisplayName = "Beta channel",
            StartedAt = started,
            FinishedAt = started.AddSeconds(83),
            BackendCommit = "0123456789abcdef",
            ExitCode = 0,
            LogPath = Path.Combine(env.Paths.LogsDirectory, "operation-1.log"),
            Result = "Succeeded",
            OldVersion = "1.0.25",
            NewVersion = "1.0.26",
            Channel = "beta",
            Duration = TimeSpan.FromSeconds(83),
        });

        var record = Assert.Single(logger.ReadHistory());

        Assert.Equal(BackendOperationKind.Repair, record.Kind);
        Assert.Equal("beta", record.Channel);
        Assert.Equal(TimeSpan.FromSeconds(83), record.Duration);
        Assert.Equal("1.0.25", record.OldVersion);
        Assert.Equal("1.0.26", record.NewVersion);
        Assert.Equal("0123456789abcdef", record.BackendCommit);
        Assert.Equal("Succeeded", record.Result);
    }

    [Fact]
    public void ExistingNumericOperationKindsRemainCompatible()
    {
        using var env = new TempEnv();
        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.HistoryFile)!);
        File.WriteAllText(env.Paths.HistoryFile,
            """
            [
              {
                "OperationId": "old-uninstall",
                "Kind": 6,
                "RequestedToken": "latest",
                "StartedAt": "2026-11-12T08:00:00+00:00",
                "FinishedAt": "2026-11-12T08:01:00+00:00",
                "Result": "Succeeded",
                "OldVersion": "1.0.24"
              }
            ]
            """);

        var record = Assert.Single(new OperationLogger(env.Paths).ReadHistory());

        Assert.Equal(BackendOperationKind.Uninstall, record.Kind);
        Assert.Null(record.Channel);
        Assert.Null(record.Duration);
    }
}
