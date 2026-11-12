using System;
using System.Collections.Generic;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Process;

namespace CircuitLord.VRModInstaller.Core.Models;

/// <summary>Verified facts about the application-managed tf2vr-linux checkout.</summary>
public sealed record BackendInstallation
{
    public required string RepositoryUrl { get; init; }
    public required string CheckoutPath { get; init; }
    public bool VerifiedOrigin { get; init; }
    public string? CommitHash { get; init; }
    public string? BranchName { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public BackendValidationStatus ValidationStatus { get; init; }
    public string InstallerPath => System.IO.Path.Combine(CheckoutPath, "install.sh");
    public string DesktopTemplatePath => System.IO.Path.Combine(CheckoutPath, "tf2vr.desktop");
    public string LauncherScriptPath => System.IO.Path.Combine(CheckoutPath, "tf2vr");
}

public enum BackendValidationStatus
{
    Unknown,
    Missing,
    CloneFailed,
    OriginMismatch,
    Incomplete,
    Validated,
}

/// <summary>
/// A backend invocation. The argument list is kept separate from any display
/// text so nothing is ever handed to a shell as one concatenated string.
/// </summary>
public sealed class BackendCommand
{
    public required string OperationId { get; init; }
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; init; } =
        new Dictionary<string, string>();
    public required string DisplayDescription { get; init; }
    public required BackendOperationKind OperationKind { get; init; }
    public TimeSpan? Timeout { get; init; }

    public string QuotedCommandLine => ArgumentListFormatter.Format(ExecutablePath, Arguments);
}

public enum BackendOperationKind
{
    Check,
    ListVersions,
    Install,
    Update,
    Reinstall,
    RefreshProton,
    Uninstall,
    BackendUpdate,
}

/// <summary>Outcome of a backend process, including the raw exit code.</summary>
public sealed class ProcessResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool Started { get; init; }
    public string? StartFailure { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }

    public bool Succeeded => Started && !TimedOut && !Cancelled && ExitCode == 0;
}

/// <summary>One selectable release in the live backend catalogue.</summary>
public sealed class VersionCatalogueEntry
{
    public int Ordinal { get; init; }
    public required string BackendToken { get; init; }
    public required string DisplayName { get; init; }
    public string? ResolvedVersion { get; init; }
    public string? ChannelId { get; init; }
    public string? Description { get; init; }
    public bool IsMovingChannel { get; init; }
    public bool IsPinnedRelease { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsSelectedByBackend { get; init; }
    public ManifestMatchStatus ManifestMatchStatus { get; set; } = ManifestMatchStatus.NotAttempted;
    public CatalogueSource CatalogueSource { get; init; } = CatalogueSource.LiveBackend;

    /// <summary>Label shown in the dropdown, e.g. "1 — Stable / Latest".</summary>
    public string DisplayLabel => Ordinal + " — " + DisplayName;
}

public enum ManifestMatchStatus
{
    NotAttempted,
    Matched,
    Unmatched,
    Ambiguous,
}

public enum CatalogueSource
{
    LiveBackend,
    Cached,
    ManifestFallback,
}

public sealed class VersionCatalogue
{
    public required IReadOnlyList<VersionCatalogueEntry> Entries { get; init; }
    public required DateTimeOffset RetrievedAt { get; init; }
    public bool IsFromCache { get; init; }
    public string? Diagnostic { get; init; }
    public string? InstalledToken { get; init; }

    public VersionCatalogueEntry? FindByToken(string token) =>
        Entries.FirstOrDefault(e => string.Equals(e.BackendToken, token, StringComparison.Ordinal));
}

/// <summary>Official manifest metadata (release notes, channels), never an install authority.</summary>
public sealed class ManifestSnapshot
{
    public int SchemaVersion { get; init; }
    public required string SourceUri { get; init; }
    public required DateTimeOffset RetrievedAt { get; init; }
    public bool IsFromCache { get; init; }
    public string? RawContentHash { get; init; }
    public string? ValidationWarning { get; init; }
    public TitanfallManifestProduct? Titanfall2Vr { get; init; }
}

public sealed class TitanfallManifestProduct
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Version { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
    public IReadOnlyList<ManifestChannel> Channels { get; init; } = Array.Empty<ManifestChannel>();
    public IReadOnlyList<ManifestChange> Changes { get; init; } = Array.Empty<ManifestChange>();
}

public sealed class ManifestChannel
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public required string Version { get; init; }
}

public sealed class ManifestChange
{
    public required string Version { get; init; }
    public string? Date { get; init; }
    public required string Notes { get; init; }
}

public enum DesktopIntegrationState
{
    NotInstalled,
    Installed,
    CategoryMissing,
    LauncherMissing,
    InvalidFile,
    DuplicateEntries,
    Error,
}

public sealed class DesktopIntegrationStatus
{
    public required string DesktopFileId { get; init; }
    public required string DesktopFilePath { get; init; }
    public string? LauncherPath { get; init; }
    public string? IconPath { get; init; }
    public bool HasRequiredCategory { get; init; }
    public bool ValidatesWithDesktopFileValidate { get; init; }
    public bool LauncherExists { get; init; }
    public IReadOnlyList<string> DuplicatePaths { get; init; } = Array.Empty<string>();
    public DesktopIntegrationState State { get; init; }
    public DateTimeOffset? LastReconciledAt { get; init; }
    public string? Detail { get; init; }
}

public enum SteamInstallationType
{
    Native,
    Flatpak,
    Custom,
}

public sealed class SteamInstallation
{
    public required string InstallationId { get; init; }
    public required SteamInstallationType InstallationType { get; init; }
    public required string LauncherCommand { get; init; }
    public required string InstallationRoot { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.Now;
    public string? FlatpakAppId { get; init; }
}

public sealed class SteamAccount
{
    public required string AccountDirectory { get; init; }
    public required string AccountId { get; init; }
    public required string ShortcutsFilePath { get; init; }
    public bool IsSelected { get; init; }
    public string? DetectionSource { get; init; }
}

public sealed class SteamShortcutRecord
{
    public string Name { get; set; } = "Titanfall 2 VR";
    public required string Executable { get; set; }
    public string StartDirectory { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string LaunchOptions { get; set; } = string.Empty;
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public int AppId { get; set; }
}

public enum SteamTransactionState
{
    Idle,
    DetectingInstallation,
    DetectingAccounts,
    CheckingActiveSessions,
    AwaitingConfirmation,
    PreparingShutdown,
    RequestingGracefulShutdown,
    WaitingForSteamExit,
    ConfirmingFileAccessAvailable,
    CreatingBackup,
    ReadingShortcuts,
    UpdatingShortcutModel,
    WritingTemporaryFile,
    VerifyingTemporaryFile,
    AtomicallyReplacingOriginal,
    VerifyingCommittedShortcut,
    RestartingSteam,
    WaitingForSteamStartup,
    VerifyingSteamRestart,
    Completed,
    CompletedWithoutLaunchingSteam,
    RecordingShortcutFailure,
    AttemptingSteamRestart,
    SteamRestartedWithIntegrationFailure,
    SteamRestartFailedNeedsRecovery,
    ShutdownTimedOut,
    CancelledWithoutChanges,
    Failed,
}

public enum ShortcutOutcome
{
    NotAttempted,
    Added,
    Updated,
    Removed,
    AlreadyCorrect,
    Failed,
}

public enum RestartOutcome
{
    NotNeeded,
    NotAttempted,
    Requested,
    Verified,
    Failed,
}

/// <summary>Durable record of a Steam close/write/reopen transaction.</summary>
public sealed class SteamRestartTransaction
{
    public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public bool SteamWasRunningBefore { get; set; }
    public string? SteamInstallationId { get; set; }
    public string? SelectedAccountId { get; set; }
    public string? OriginalShortcutsPath { get; set; }
    public string? BackupPath { get; set; }
    public string? TemporaryPath { get; set; }
    public string? ShortcutIdentity { get; set; }
    public DateTimeOffset? ShutdownRequestedAt { get; set; }
    public DateTimeOffset? SteamExitObservedAt { get; set; }
    public DateTimeOffset? FileWriteStartedAt { get; set; }
    public DateTimeOffset? FileWriteCompletedAt { get; set; }
    public DateTimeOffset? SteamRelaunchRequestedAt { get; set; }
    public DateTimeOffset? SteamRelaunchObservedAt { get; set; }
    public bool RestartOwed { get; set; }
    public SteamTransactionState State { get; set; } = SteamTransactionState.Idle;
    public ShortcutOutcome ShortcutResult { get; set; } = ShortcutOutcome.NotAttempted;
    public RestartOutcome RestartResult { get; set; } = RestartOutcome.NotNeeded;
    public bool RecoveryRequired { get; set; }
    public List<string> Timeline { get; set; } = new();
}

public sealed class SteamTransactionResult
{
    public required SteamRestartTransaction Transaction { get; init; }
    public required ShortcutOutcome Shortcut { get; init; }
    public required RestartOutcome Restart { get; init; }
    public string? Message { get; init; }
    public bool ShortcutVerified { get; init; }
}

public sealed class InstallerOperationRecord
{
    public required string OperationId { get; init; }
    public required BackendOperationKind Kind { get; init; }
    public required string RequestedToken { get; init; }
    public string? RequestedDisplayName { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? BackendCommit { get; init; }
    public int? ExitCode { get; set; }
    public string? LogPath { get; set; }
    public string Result { get; set; } = "Running";
    public string? OldVersion { get; set; }
    public string? NewVersion { get; set; }
}
