using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Steam;

public enum ShortcutInstallState
{
    NotChecked,
    NotInstalled,
    Installed,
    ConflictingEntry,
}

public sealed class SteamShortcutStatus
{
    public required ShortcutInstallState State { get; init; }
    public required string ShortcutsFilePath { get; init; }
    public string? Detail { get; init; }
    public bool SteamAccountHasNoShortcutFile { get; init; }
    public int OwnedEntryCount { get; init; }
}

/// <summary>
/// Owns the Titanfall 2 VR Steam shortcut and the close → edit → reopen
/// transaction.
///
/// Core invariants implemented here:
///  - a shortcut file is only ever written when the selected Steam client is
///    observed to be stopped;
///  - every shortcut write is backed up, written to a same-filesystem temporary
///    file, validated, then atomically committed and re-parsed;
///  - if this application shut Steam down, it attempts to reopen the same
///    installation on every terminal path, including shortcut failures;
///  - Steam is never force-killed and no timeout path writes the file.
/// </summary>
public sealed class SteamShortcutManager
{
    public const string ShortcutName = "Titanfall 2 VR";

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly XdgPaths _paths;
    private readonly ISteamProcessController _steam;
    private readonly Action<string>? _log;

    public SteamShortcutManager(XdgPaths paths, ISteamProcessController steam, Action<string>? log = null)
    {
        _paths = paths;
        _steam = steam;
        _log = log;
    }

    public SteamShortcutStatus Inspect(SteamAccount account, string launcherPath)
    {
        var path = account.ShortcutsFilePath;
        if (!File.Exists(path))
        {
            return new SteamShortcutStatus
            {
                State = ShortcutInstallState.NotInstalled,
                ShortcutsFilePath = path,
                SteamAccountHasNoShortcutFile = true,
                Detail = "This account has no shortcuts.vdf yet; Steam creates it the first time a non-Steam game is added.",
            };
        }

        try
        {
            var document = SteamShortcutsDocument.Load(path);
            var match = document.Match(ShortcutName, launcherPath, out var entries);
            return new SteamShortcutStatus
            {
                State = match switch
                {
                    ShortcutMatch.Owned => ShortcutInstallState.Installed,
                    ShortcutMatch.Ambiguous => ShortcutInstallState.ConflictingEntry,
                    _ => ShortcutInstallState.NotInstalled,
                },
                ShortcutsFilePath = path,
                OwnedEntryCount = match == ShortcutMatch.Owned ? entries.Count : 0,
                Detail = match == ShortcutMatch.Ambiguous
                    ? "A shortcut named Titanfall 2 VR exists but launches a different program."
                    : null,
            };
        }
        catch (Exception ex) when (ex is VdfFormatException or IOException or InvalidDataException)
        {
            return new SteamShortcutStatus
            {
                State = ShortcutInstallState.ConflictingEntry,
                ShortcutsFilePath = path,
                Detail = "The shortcut file could not be read: " + ex.Message,
            };
        }
    }

    public SteamShortcutRecord CreateRecord(string launcherPath) => new()
    {
        Name = ShortcutName,
        Executable = launcherPath,
        StartDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty,
        Icon = "applications-games",
        LaunchOptions = string.Empty,
        Tags = new[] { "VR" },
    };

    /// <summary>
    /// Adds or updates the shortcut. <paramref name="allowSteamRestart"/> must be
    /// true only after the user confirmed the restart dialog.
    /// </summary>
    public async Task<SteamTransactionResult> AddOrUpdateAsync(
        SteamInstallation installation,
        SteamAccount account,
        string launcherPath,
        bool allowSteamRestart,
        CancellationToken cancellationToken = default)
    {
        var transaction = new SteamRestartTransaction
        {
            State = SteamTransactionState.DetectingInstallation,
            SteamInstallationId = installation.InstallationId,
            SelectedAccountId = account.AccountId,
            OriginalShortcutsPath = account.ShortcutsFilePath,
            ShortcutIdentity = launcherPath,
        };

        var wasRunning = _steam.IsClientRunning(installation);
        transaction.SteamWasRunningBefore = wasRunning;
        transaction.State = SteamTransactionState.CheckingActiveSessions;
        Add(transaction, "Steam detected: " + installation.InstallationType + " at " + installation.InstallationRoot);
        Add(transaction, "Steam was " + (wasRunning ? "running" : "closed"));

        if (wasRunning && !allowSteamRestart)
        {
            transaction.State = SteamTransactionState.CancelledWithoutChanges;
            await SaveJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
            return Finish(transaction, ShortcutOutcome.NotAttempted, RestartOutcome.NotNeeded,
                "The shortcut was not changed because Steam was not allowed to restart.");
        }

        var shortcutOutcome = ShortcutOutcome.NotAttempted;
        var shortcutVerified = false;
        string? failure = null;

        try
        {
            if (wasRunning)
            {
                transaction.State = SteamTransactionState.RequestingGracefulShutdown;
                transaction.ShutdownRequestedAt = DateTimeOffset.Now;
                Add(transaction, "Requesting a graceful Steam shutdown");
                await SaveJournalAsync(transaction, cancellationToken).ConfigureAwait(false);

                await _steam.RequestGracefulShutdownAsync(installation, cancellationToken).ConfigureAwait(false);

                transaction.State = SteamTransactionState.WaitingForSteamExit;
                await SaveJournalAsync(transaction, cancellationToken).ConfigureAwait(false);

                var exited = await _steam.WaitForExitAsync(installation, ShutdownTimeout, cancellationToken).ConfigureAwait(false);
                if (!exited)
                {
                    transaction.State = SteamTransactionState.ShutdownTimedOut;
                    Add(transaction, "Steam did not exit within the timeout; shortcut file left untouched");
                    await SaveJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
                    return Finish(transaction, ShortcutOutcome.NotAttempted, RestartOutcome.NotNeeded,
                        "Steam did not close on its own, so the shortcut file was left untouched. Nothing was force-killed.");
                }

                transaction.SteamExitObservedAt = DateTimeOffset.Now;
                transaction.RestartOwed = true;
                Add(transaction, "Steam exit observed");
            }

            // From here on, a restart is owed whenever we closed Steam.
            await SaveJournalAsync(transaction, cancellationToken).ConfigureAwait(false);

            transaction.State = SteamTransactionState.ConfirmingFileAccessAvailable;
            if (_steam.IsClientRunning(installation))
            {
                failure = "Steam started again before the shortcut file was written.";
                shortcutOutcome = ShortcutOutcome.Failed;
            }
            else
            {
                (shortcutOutcome, shortcutVerified) = await WriteShortcutAsync(transaction, account, launcherPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            failure = "The Steam transaction was cancelled.";
            shortcutOutcome = ShortcutOutcome.Failed;
            transaction.State = SteamTransactionState.Failed;
        }
        catch (Exception ex) when (ex is VdfFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = ex.Message;
            shortcutOutcome = ShortcutOutcome.Failed;
            transaction.State = SteamTransactionState.Failed;
        }
        finally
        {
            if (transaction.RestartOwed)
            {
                transaction.State = SteamTransactionState.AttemptingSteamRestart;
                Add(transaction, "Reopening the same Steam installation");
                await SaveJournalAsync(transaction, CancellationToken.None).ConfigureAwait(false);

                transaction.SteamRelaunchRequestedAt = DateTimeOffset.Now;
                var launched = await _steam.LaunchAsync(installation, CancellationToken.None).ConfigureAwait(false);
                var started = launched && await _steam.WaitForStartupAsync(installation, StartupTimeout, CancellationToken.None)
                    .ConfigureAwait(false);

                if (started)
                {
                    transaction.SteamRelaunchObservedAt = DateTimeOffset.Now;
                    transaction.RestartResult = RestartOutcome.Verified;
                    transaction.RestartOwed = false;
                    Add(transaction, "Steam restart verified");
                    transaction.State = shortcutOutcome == ShortcutOutcome.Failed
                        ? SteamTransactionState.SteamRestartedWithIntegrationFailure
                        : SteamTransactionState.Completed;
                }
                else
                {
                    transaction.RestartResult = RestartOutcome.Failed;
                    transaction.RecoveryRequired = true;
                    Add(transaction, "Steam did not come back up");
                    transaction.State = SteamTransactionState.SteamRestartFailedNeedsRecovery;
                }
            }
            else
            {
                // Never overwrite a deliberate terminal state (shutdown timeout,
                // user refusal). Only paths that actually reached the write stage
                // get a completion state here.
                if (shortcutOutcome == ShortcutOutcome.Failed)
                {
                    transaction.State = SteamTransactionState.Failed;
                }
                else if (transaction.State is not (SteamTransactionState.ShutdownTimedOut
                            or SteamTransactionState.CancelledWithoutChanges
                            or SteamTransactionState.Failed))
                {
                    transaction.State = SteamTransactionState.CompletedWithoutLaunchingSteam;
                }
                transaction.RestartResult = RestartOutcome.NotNeeded;
            }

            await SaveJournalAsync(transaction, CancellationToken.None).ConfigureAwait(false);
        }

        var message = BuildMessage(shortcutOutcome, transaction.RestartResult, failure);
        return Finish(transaction, shortcutOutcome, transaction.RestartResult, message, shortcutVerified);
    }

    /// <summary>Removal uses the same transaction guarantees and only touches our entry.</summary>
    public async Task<SteamTransactionResult> RemoveAsync(
        SteamInstallation installation,
        SteamAccount account,
        string launcherPath,
        bool allowSteamRestart,
        CancellationToken cancellationToken = default)
    {
        var transaction = new SteamRestartTransaction
        {
            SelectedAccountId = account.AccountId,
            SteamInstallationId = installation.InstallationId,
            OriginalShortcutsPath = account.ShortcutsFilePath,
            ShortcutIdentity = launcherPath,
            SteamWasRunningBefore = _steam.IsClientRunning(installation),
        };

        if (transaction.SteamWasRunningBefore && !allowSteamRestart)
        {
            transaction.State = SteamTransactionState.CancelledWithoutChanges;
            return Finish(transaction, ShortcutOutcome.NotAttempted, RestartOutcome.NotNeeded,
                "Steam must be closed or allowed to restart before the shortcut can be removed.");
        }

        if (transaction.SteamWasRunningBefore)
        {
            await _steam.RequestGracefulShutdownAsync(installation, cancellationToken).ConfigureAwait(false);
            if (!await _steam.WaitForExitAsync(installation, ShutdownTimeout, cancellationToken).ConfigureAwait(false))
            {
                transaction.State = SteamTransactionState.ShutdownTimedOut;
                return Finish(transaction, ShortcutOutcome.NotAttempted, RestartOutcome.NotNeeded,
                    "Steam did not close, so the shortcut was left in place.");
            }
            transaction.RestartOwed = true;
        }

        try
        {
            if (!File.Exists(account.ShortcutsFilePath))
            {
                transaction.State = SteamTransactionState.CompletedWithoutLaunchingSteam;
                return Finish(transaction, ShortcutOutcome.AlreadyCorrect, RestartOutcome.NotNeeded, "No shortcut file exists.");
            }

            Backup(transaction);
            var document = SteamShortcutsDocument.Load(account.ShortcutsFilePath);
            var removed = document.RemoveOwned(ShortcutName, launcherPath);
            if (removed == 0)
            {
                transaction.State = SteamTransactionState.CompletedWithoutLaunchingSteam;
                return Finish(transaction, ShortcutOutcome.AlreadyCorrect, RestartOutcome.NotNeeded,
                    "No Titanfall 2 VR shortcut owned by this installer was found.");
            }

            transaction.State = SteamTransactionState.WritingTemporaryFile;
            document.SaveVerified(account.ShortcutsFilePath);
            transaction.State = SteamTransactionState.VerifyingCommittedShortcut;
            var check = SteamShortcutsDocument.Load(account.ShortcutsFilePath);
            var stillThere = check.Match(ShortcutName, launcherPath, out _) == ShortcutMatch.Owned;

            return Finish(transaction, stillThere ? ShortcutOutcome.Failed : ShortcutOutcome.Removed,
                RestartOutcome.NotNeeded,
                stillThere ? "The shortcut is still present after the rewrite." : "The Titanfall 2 VR shortcut was removed.");
        }
        finally
        {
            if (transaction.RestartOwed)
            {
                var launched = await _steam.LaunchAsync(installation, CancellationToken.None).ConfigureAwait(false);
                var started = launched && await _steam.WaitForStartupAsync(installation, StartupTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                transaction.RestartResult = started ? RestartOutcome.Verified : RestartOutcome.Failed;
                transaction.RecoveryRequired = !started;
                transaction.RestartOwed = false;
                await SaveJournalAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns true when an earlier transaction shut Steam down and never
    /// observed it come back. The UI turns this into an explicit recovery action
    /// instead of launching Steam behind the user's back.
    /// </summary>
    public IReadOnlyList<SteamRestartTransaction> PendingRecoveries()
    {
        var results = new List<SteamRestartTransaction>();
        if (!Directory.Exists(_paths.SteamJournalDirectory))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.SteamJournalDirectory, "*.json"))
        {
            try
            {
                var transaction = JsonSerializer.Deserialize<SteamRestartTransaction>(File.ReadAllText(file));
                if (transaction is { RestartOwed: true })
                {
                    results.Add(transaction);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A corrupt journal is ignored; it never triggers an automatic launch.
            }
        }
        return results;
    }

    private async Task<(ShortcutOutcome Outcome, bool Verified)> WriteShortcutAsync(
        SteamRestartTransaction transaction,
        SteamAccount account,
        string launcherPath,
        CancellationToken cancellationToken)
    {
        transaction.State = SteamTransactionState.CreatingBackup;
        Backup(transaction);

        transaction.State = SteamTransactionState.ReadingShortcuts;
        var document = File.Exists(account.ShortcutsFilePath)
            ? SteamShortcutsDocument.Load(account.ShortcutsFilePath)
            : SteamShortcutsDocument.Empty();

        transaction.State = SteamTransactionState.UpdatingShortcutModel;
        var before = document.Match(ShortcutName, launcherPath, out _);
        document.Upsert(CreateRecord(launcherPath));

        transaction.State = SteamTransactionState.WritingTemporaryFile;
        transaction.FileWriteStartedAt = DateTimeOffset.Now;
        document.SaveVerified(account.ShortcutsFilePath);
        transaction.FileWriteCompletedAt = DateTimeOffset.Now;

        transaction.State = SteamTransactionState.VerifyingCommittedShortcut;
        var committed = SteamShortcutsDocument.Load(account.ShortcutsFilePath);
        var match = committed.Match(ShortcutName, launcherPath, out var entries);
        var verified = match == ShortcutMatch.Owned && entries.Count == 1;

        await SaveJournalAsync(transaction, cancellationToken).ConfigureAwait(false);

        if (!verified)
        {
            return (ShortcutOutcome.Failed, false);
        }

        return (before == ShortcutMatch.Owned ? ShortcutOutcome.Updated : ShortcutOutcome.Added, true);
    }

    private void Backup(SteamRestartTransaction transaction)
    {
        if (transaction.OriginalShortcutsPath is null || !File.Exists(transaction.OriginalShortcutsPath))
        {
            return;
        }

        var backup = transaction.OriginalShortcutsPath + ".backup-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(backup))
        {
            backup += "-" + Guid.NewGuid().ToString("N")[..4];
        }
        File.Copy(transaction.OriginalShortcutsPath, backup, overwrite: false);
        transaction.BackupPath = backup;
        Add(transaction, "Shortcut file backed up to " + backup);
    }

    private async Task SaveJournalAsync(SteamRestartTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.SteamJournalDirectory);
            var path = Path.Combine(_paths.SteamJournalDirectory, transaction.TransactionId + ".json");
            var json = JsonSerializer.Serialize(transaction, new JsonSerializerOptions { WriteIndented = true });
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The journal must never crash the transaction; the loss is logged.
            _log?.Invoke("Could not persist the Steam transaction journal: " + ex.Message);
        }
    }

    private SteamTransactionResult Finish(
        SteamRestartTransaction transaction,
        ShortcutOutcome shortcut,
        RestartOutcome restart,
        string message,
        bool verified = false)
    {
        transaction.ShortcutResult = shortcut;
        transaction.RestartResult = restart;
        Add(transaction, message);
        _log?.Invoke(message);
        return new SteamTransactionResult
        {
            Transaction = transaction,
            Shortcut = shortcut,
            Restart = restart,
            Message = message,
            ShortcutVerified = verified,
        };
    }

    private static string BuildMessage(ShortcutOutcome shortcut, RestartOutcome restart, string? failure)
    {
        var shortcutText = shortcut switch
        {
            ShortcutOutcome.Added => "Titanfall 2 VR was added to Steam.",
            ShortcutOutcome.Updated => "The Titanfall 2 VR Steam shortcut was updated.",
            ShortcutOutcome.Removed => "The Titanfall 2 VR Steam shortcut was removed.",
            ShortcutOutcome.AlreadyCorrect => "The Steam shortcut was already correct.",
            ShortcutOutcome.Failed => "The Steam shortcut could not be written" + (failure is null ? "." : ": " + failure),
            _ => failure ?? "The Steam shortcut was not changed.",
        };

        var restartText = restart switch
        {
            RestartOutcome.Verified => "Steam reopened.",
            RestartOutcome.Failed => "Steam did not reopen; use Start Steam to launch it again.",
            RestartOutcome.NotNeeded => string.Empty,
            _ => "Steam restart was not requested.",
        };

        return string.Join(' ', new[] { shortcutText, restartText }.Where(s => s.Length > 0));
    }

    private static void Add(SteamRestartTransaction transaction, string entry) =>
        transaction.Timeline.Add(DateTimeOffset.Now.ToString("HH:mm:ss") + "  " + entry);
}
