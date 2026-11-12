using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core;
using CircuitLord.VRModInstaller.Core.Backend;
using CircuitLord.VRModInstaller.Core.Catalogue;
using CircuitLord.VRModInstaller.Core.Desktop;
using CircuitLord.VRModInstaller.Core.Logging;
using CircuitLord.VRModInstaller.Core.Manifest;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Process;
using CircuitLord.VRModInstaller.Core.Runtime;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Core.Steam;
using CircuitLord.VRModInstaller.Core.Updates;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

public sealed class VersionOption
{
    public required string DisplayLabel { get; init; }
    public required string Token { get; init; }
    public required string? Version { get; init; }
    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Presentation state for the ported window. Every action here calls a real
/// service: no button is wired to a placeholder.
/// </summary>
public sealed class InstallerViewModel
{
    private readonly XdgPaths _paths = new();
    private readonly ProcessRunner _runner = new();
    private readonly OperationLogger _logger;
    private readonly BackendManager _backend;
    private readonly InstallScriptAdapter _adapter;
    private readonly VersionCatalogueService _catalogueService;
    private readonly ManifestService _manifest;
    private readonly DesktopEntryManager _desktop;
    private readonly LauncherService _launcher;
    private readonly RuntimeService _runtime;
    private readonly SettingsStore _settingsStore;
    private readonly SteamLocator _steamLocator;
    private readonly ISteamProcessController _steamProcess;
    private readonly SteamShortcutManager _steamManager;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly ReleaseChecker _releaseChecker = new();
    private static readonly Version CurrentVersion =
        typeof(InstallerViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
    private bool _checkingUpdates;
    private bool _checkingBackendUpdate;
    private bool _backendUpdateInProgress;
    private string? _latestAppTag;
    private BackendUpdateCheck? _backendUpdateCheck;
    private OpenXrRuntimeInfo? _runtimeInfo;
    private DesktopIntegrationStatus? _desktopStatus;
    private SteamShortcutStatus? _steamShortcutStatus;
    private IReadOnlyDictionary<string, string> _backendPaths =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private DateTimeOffset? _lastPreflightAt;
    private bool _lastPreflightSucceeded;
    private string? _installedVersionRaw;
    private string? _installedToken;
    private InstallerOperationRecord? _lastOperation;

    private BackendInstallation? _backendState;
    private AppSettings _settings = new();
    private SteamInstallation? _steamInstallation;
    private SteamAccount? _steamAccount;
    private VersionCatalogue? _catalogue;

    public InstallerViewModel()
    {
        _logger = new OperationLogger(_paths);
        _backend = new BackendManager(_paths, _runner);
        _adapter = new InstallScriptAdapter(_paths);
        _catalogueService = new VersionCatalogueService(
            _paths, _runner, _adapter,
            new OperationLoggerAccessor { Log = line => AddLog(line) });
        _manifest = new ManifestService(_paths);
        _desktop = new DesktopEntryManager(_paths);
        _launcher = new LauncherService(_paths);
        _runtime = new RuntimeService(_paths);
        _settingsStore = new SettingsStore(_paths);
        _steamLocator = new SteamLocator(_paths);
        _steamProcess = new SteamProcessController();
        _steamManager = new SteamShortcutManager(_paths, _steamProcess, line => AddLog(line));
        _settings = _settingsStore.Load();
    }

    public event EventHandler? Changed;
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<string> ChangelogLines { get; } = new();

    public string AppTitle => "CircuitLord's VR Mod Installer";
    public string CurrentAppVersion => CurrentVersion.ToString(3);
    public string AppVersion => CurrentAppVersion + " · Linux";
    public string PlatformText => "Linux · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription
        + " · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

    public bool RoundedCorners => _settings.RoundedCorners;
    public string Language => _settings.Language;
    public bool AutoCheckForAppUpdates => _settings.CheckForAppUpdates;
    public bool UpdateBackendAutomatically => _settings.UpdateBackendAutomatically;
    public bool ConfirmSteamRestart => _settings.ConfirmSteamRestart;
    public string? LatestReleaseUrl { get; private set; }
    public bool UpdateAvailable { get; private set; }
    public string UpdateStatusText { get; private set; } = Loc.Get("updatesNotChecked");
    public string LatestAppVersionText => _latestAppTag ?? Loc.Get("notChecked");

    public string BackendCurrentCommitText => _backendState?.ValidationStatus == BackendValidationStatus.Validated
        ? Short(_backendState.CommitHash) + " · " + Loc.Get("originVerified")
        : _backendState?.ValidationStatus.ToString() ?? Loc.Get("notChecked");
    public string BackendLatestCommitText => _backendUpdateCheck?.RemoteCommit is { Length: >= 7 } remote
        ? Short(remote) + (_backendUpdateCheck.UpdateAvailable ? string.Empty : " · " + Loc.Get("upToDateShort"))
        : Loc.Get("notChecked");
    public bool BackendUpdateAvailable => _backendUpdateCheck?.UpdateAvailable == true;
    public bool BackendCheckInProgress => _checkingBackendUpdate;
    public bool BackendProgressVisible => _checkingBackendUpdate || _backendUpdateInProgress;
    public string BackendWorkingTreeText => _backendUpdateCheck is null
        ? Loc.Get("notChecked")
        : _backendUpdateCheck.WorkingTreeClean ? Loc.Get("clean") : Loc.Get("dirty");
    public string BackendUpdateStatusText { get; private set; } = Loc.Get("backendUpdateNotChecked");
    public string BackendLastUpdateText => _settings.LastBackendUpdateAt is null
        ? Loc.Get("never")
        : _settings.LastBackendUpdateAt.Value.ToLocalTime().ToString("g")
          + (_settings.LastBackendUpdateResult is null ? string.Empty : " · " + _settings.LastBackendUpdateResult);
    public string BackendLastUpdateSummary => Loc.Format("helperRefreshed", BackendLastUpdateText);
    public string BackendRecoveryText => _backendUpdateCheck?.Error is not null
        ? _backendUpdateCheck.Error
        : Loc.Get("backendRecoveryInfo");

    public bool SteamDetected => _steamInstallation is not null;
    public string SteamStatusText => _steamInstallation is null ? Loc.Get("notDetected") : Loc.Get("detected");
    public string SteamInstallationText => _steamInstallation is null
        ? Loc.Get("steamNotDetected")
        : _steamInstallation.InstallationRoot;
    public string SteamTypeText => _steamInstallation?.InstallationType.ToString() ?? Loc.Get("notDetected");
    public string SteamAccountText => _steamAccount?.AccountId ?? Loc.Get("notChecked");
    public string SteamShortcutExecutableText => _steamShortcutStatus?.Executable is { Length: > 0 } executable
        ? executable + (string.IsNullOrWhiteSpace(_steamShortcutStatus.LaunchOptions)
            ? string.Empty
            : " " + _steamShortcutStatus.LaunchOptions)
        : _launcher.LauncherExists ? _launcher.LauncherPath : Loc.Get("launcherMissing");
    public bool SteamShortcutInstalled => _steamShortcutStatus?.State == ShortcutInstallState.Installed;
    public string SteamShortcutStatusText => _steamShortcutStatus is null
        ? Loc.Get("notChecked")
        : (_steamShortcutStatus.State switch
        {
            ShortcutInstallState.Installed => Loc.Get("shortcutInstalled"),
            ShortcutInstallState.ConflictingEntry => Loc.Get("shortcutConflicting"),
            ShortcutInstallState.NotInstalled => Loc.Get("shortcutNotInstalled"),
            _ => Loc.Get("notChecked"),
        })
        + DescribeShortcutExtra();
    private string DescribeShortcutExtra()
    {
        if (_steamShortcutStatus is null)
        {
            return string.Empty;
        }
        if (_steamShortcutStatus.State == ShortcutInstallState.Installed
            && !string.Equals(_steamShortcutStatus.Executable, _launcher.LauncherPath, StringComparison.Ordinal))
        {
            return " · " + Loc.Get("shortcutLegacyWrapper");
        }
        if (_steamShortcutStatus.State == ShortcutInstallState.ConflictingEntry)
        {
            return " · " + Loc.Get("shortcutConflictDetail");
        }
        return _steamShortcutStatus.Detail is null ? string.Empty : " · " + _steamShortcutStatus.Detail;
    }

    public string SteamLastOperationText => _settings.LastSteamIntegrationAt is null
        ? Loc.Get("never")
        : _settings.LastSteamIntegrationAt.Value.ToLocalTime().ToString("g")
          + (_settings.LastSteamIntegrationResult is null ? string.Empty : " · " + _settings.LastSteamIntegrationResult);

    public string OpenXrStatusText => _runtimeInfo is null
        ? Loc.Get("notChecked")
        : (_runtimeInfo.RuntimeName ?? Loc.Get("noRuntimeRegistered"))
          + (_runtimeInfo.RuntimePath is null ? string.Empty : " · " + _runtimeInfo.RuntimePath);
    public string SteamVrStatusText => _runtimeInfo is null
        ? Loc.Get("notChecked")
        : _runtimeInfo.SteamVrAvailable
            ? (_runtimeInfo.SteamVrPath ?? Loc.Get("detected")) + (_runtimeInfo.SteamVrRunning ? " · " + Loc.Get("running") : string.Empty)
            : Loc.Get("notDetected");
    public string WiVrnStatusText => _runtimeInfo is null
        ? Loc.Get("notChecked")
        : _runtimeInfo.WiVrnAvailable
            ? Loc.Get("detected") + (_runtimeInfo.WiVrnRunning ? " · " + Loc.Get("running") : string.Empty)
            : Loc.Get("notDetected");
    public string MonadoStatusText => _runtimeInfo is null
        ? Loc.Get("notChecked")
        : _runtimeInfo.MonadoAvailable
            ? Loc.Get("detected") + (_runtimeInfo.MonadoRunning ? " · " + Loc.Get("running") : string.Empty)
            : Loc.Get("notDetected");
    public string OpenVrBridgeStatusText => _runtimeInfo?.OpenVrBridgePath is null
        ? Loc.Get("bridgeMissing")
        : (_runtimeInfo.OpenVrBridgeKind ?? Loc.Get("detected")) + " · " + _runtimeInfo.OpenVrBridgePath;
    public string LauncherStatusText => _launcher.LauncherExists
        ? _launcher.LauncherPath
        : Loc.Get("launcherMissing");
    public string DesktopEntryStatusText => _desktopStatus is null
        ? Loc.Get("notChecked")
        : DescribeDesktop(_desktopStatus);
    public string RuntimeDetailsText => BuildRuntimeDetails();
    public string LastPreflightText => _lastPreflightAt is null
        ? Loc.Get("preflightNever")
        : (_lastPreflightSucceeded ? Loc.Get("preflightPassed") : Loc.Get("preflightFailed"))
          + " · " + _lastPreflightAt.Value.ToLocalTime().ToString("g");
    public string LogsDirectory => _paths.LogsDirectory;
    public string LauncherLogPath => Path.Combine(_paths.StateHome, "tf2vr", "launch.log");
    public string DiagnosticsReport => BuildDiagnosticsReport();

    public string BackendText { get; private set; } = "Checking the Linux backend...";
    public string CatalogueText { get; private set; } = "Loading version catalogue...";
    public string InstalledText { get; private set; } = "Not installed";
    public string InstalledVersionText => IsInstalled
        ? ModVersion(_installedVersionRaw) ?? Loc.Get("versionUnknown")
        : Loc.Get("notInstalled");
    public string InstalledChannelText => !IsInstalled
        ? Loc.Get("notInstalled")
        : _installedToken switch
        {
            "latest" => Loc.Get("channelStable"),
            "beta" => Loc.Get("channelBeta"),
            null or "" => Loc.Get("channelUnknown"),
            var token when token.StartsWith("beta-", StringComparison.Ordinal) => Loc.Get("channelPinned") + " · " + token,
            var token => Loc.Get("channelPinned") + " · " + token,
        };
    public string LatestStableVersionText =>
        _catalogue?.FindByToken("latest")?.ResolvedVersion ?? Loc.Get("notAvailable");
    public string LatestBetaVersionText =>
        _catalogue?.FindByToken("beta")?.ResolvedVersion ?? Loc.Get("notAvailable");
    public string LastOperationText => _lastOperation is null
        ? Loc.Get("noInstallRecord")
        : (_lastOperation.FinishedAt ?? _lastOperation.StartedAt).ToLocalTime().ToString("g")
          + " · "
          + (_lastOperation.Result switch
          {
              "Succeeded" => Loc.Get("operationSucceeded"),
              "Running" => Loc.Get("operationRunning"),
              _ => Loc.Get("operationFailed"),
          })
          + (_lastOperation.NewVersion is null
              ? string.Empty
              : " · " + (ModVersion(_lastOperation.NewVersion) ?? _lastOperation.NewVersion));
    public string ChangelogPreview => ChangelogLines.Count == 0
        ? Loc.Get("changelogUnavailable")
        : string.Join('\n', ChangelogLines.Take(4)).Trim();
    public bool CanReinstall => IsInstalled && CanInstall;
    public bool CanUpdateSelected => IsInstalled
        && CanInstall
        && SelectedVersion is not null
        && (!string.Equals(ModVersion(_installedVersionRaw), SelectedVersion.Version, StringComparison.Ordinal)
            || (SelectedVersion.Version is null && !string.Equals(_installedToken, SelectedVersion.Token, StringComparison.Ordinal)));
    public string GameText { get; private set; } = "Titanfall 2 VR install state unknown.";
    public string PreflightText { get; private set; } = "Run the backend check to see game, Proton and runtime details.";
    public string DesktopText { get; private set; } = "WiVRn desktop entry: not checked.";
    public string SteamText { get; private set; } = "Steam: not checked.";
    public string RuntimeText { get; private set; } = "OpenXR runtime: not checked.";
    public string ManifestText { get; private set; } = string.Empty;
    public string StageText { get; private set; } = "Starting";
    public string PrimaryActionLabel { get; private set; } = "Install";
    public string BigWalkText { get; private set; } = "Not available on Linux — no installation backend exists for Big Walk VR.";

    public bool Busy { get; private set; }
    public bool CanInstall { get; private set; }
    public bool CanLaunch { get; private set; }
    public bool CanUninstall { get; private set; }
    public bool IsInstalled { get; private set; }
    public bool IsSteamRunning => _steamInstallation is not null && _steamProcess.IsClientRunning(_steamInstallation);
    public bool HasSteamAccount => _steamAccount is not null;
    public VersionOption? SelectedVersion { get; set; }
    public List<VersionOption> Versions { get; private set; } = new();

    public async Task InitializeAsync()
    {
        SetStage("Checking the Linux backend");

        _backendState = await _backend.EnsureAsync(_lifetime.Token);
        BackendText = _backendState.ValidationStatus == BackendValidationStatus.Validated
            ? $"tf2vr-linux {Short(_backendState.CommitHash)} · origin verified"
            : _backendState.ValidationStatus switch
            {
                BackendValidationStatus.CloneFailed => "Backend clone failed (network or disk).",
                BackendValidationStatus.OriginMismatch => "Existing checkout has a different origin; left untouched.",
                BackendValidationStatus.Incomplete => "Backend checkout is missing install.sh or tf2vr.",
                _ => "Backend not available yet.",
            };

        if (_settings.UpdateBackendAutomatically &&
            _backendState.ValidationStatus == BackendValidationStatus.Validated)
        {
            AddLog("Automatically updating the tf2vr-linux helper.");
            await UpdateBackendAsync();
        }

        // Cached catalogue first so the window is useful immediately.
        var cached = _catalogueService.LoadCache();
        if (cached is not null)
        {
            ApplyCatalogue(cached);
        }

        if (_backendState.ValidationStatus == BackendValidationStatus.Validated)
        {
            await RefreshCatalogueAsync();
            await LoadManifestAsync();
        }

        RefreshLocalState();
        SetStage("Ready");
        RaiseChanged();

        if (_settings.CheckForAppUpdates)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    private async Task LoadManifestAsync()
    {
        var snapshot = await _manifest.GetAsync(forceRefresh: false, _lifetime.Token);
        ChangelogLines.Clear();
        if (snapshot.Titanfall2Vr is null)
        {
            ManifestText = snapshot.ValidationWarning ?? "Release notes unavailable.";
            return;
        }

        var product = snapshot.Titanfall2Vr;
        foreach (var change in product.Changes)
        {
            ChangelogLines.Add($"v{change.Version}{(change.Date is null ? string.Empty : "  " + change.Date)}");
            ChangelogLines.Add(change.Notes);
            ChangelogLines.Add(string.Empty);
        }

        ManifestText = (snapshot.IsFromCache ? "Release notes from cache" : "Release notes current")
            + $" · stable {product.Version}"
            + (product.Channels.Count > 0 ? $" · beta {product.Channels[0].Version}" : string.Empty);
        if (snapshot.ValidationWarning is not null)
        {
            ManifestText += " · " + snapshot.ValidationWarning;
        }
    }

    public async Task RefreshCatalogueAsync()
    {
        if (_backendState is null || _backendState.ValidationStatus != BackendValidationStatus.Validated)
        {
            CatalogueText = "Catalogue unavailable until the backend checkout is valid.";
            return;
        }

        var previousSelection = SelectedVersion?.Token ?? _settings.SelectedToken;
        var installedToken = _backend.ReadSelectionToken();
        var catalogue = await _catalogueService.RefreshAsync(_backendState, installedToken, _lifetime.Token);

        ApplyCatalogue(catalogue);

        // Preserve the user's token across a catalogue reorder.
        var keep = previousSelection is not null
            ? Versions.FirstOrDefault(v => v.Token == previousSelection && catalogue.FindByToken(previousSelection) is not null)
            : null;
        SelectedVersion = keep ?? Versions.FirstOrDefault();
        if (keep is null && previousSelection is not null)
        {
            AddLog($"'{previousSelection}' is no longer offered by the backend; selection reset to the first entry.");
        }

        CatalogueText = catalogue.Entries.Count == 0
            ? catalogue.Diagnostic ?? "The backend returned no installable versions."
            : $"{(catalogue.IsFromCache ? "Cached" : "Live")} catalogue · {catalogue.Entries.Count} choices"
              + (catalogue.Diagnostic is null ? string.Empty : " · " + catalogue.Diagnostic);

        UpdatePrimaryAction();
        RaiseChanged();
    }

    private void ApplyCatalogue(VersionCatalogue catalogue)
    {
        _catalogue = catalogue;
        Versions = catalogue.Entries
            .Select(e => new VersionOption
            {
                DisplayLabel = e.DisplayLabel + (e.ResolvedVersion is null ? string.Empty : $"  ({e.ResolvedVersion})"),
                Token = e.BackendToken,
                Version = e.ResolvedVersion,
            })
            .ToList();

        if (SelectedVersion is null && Versions.Count > 0)
        {
            SelectedVersion = Versions.FirstOrDefault(v => v.Token == _settings.SelectedToken) ?? Versions[0];
        }
    }

    public async Task RunPreflightAsync()
    {
        if (!TryEnter(nameof(RunPreflightAsync), out _))
        {
            return;
        }
        try
        {
            if (_backendState is null || _backendState.ValidationStatus != BackendValidationStatus.Validated)
            {
                PreflightText = "The backend is not validated, so nothing was run.";
                return;
            }

            SetStage("Running backend preflight");
            var command = _adapter.CreateCheck(_backendState);
            var output = new List<string>();
            var result = await _runner.RunAsync(
                command.ExecutablePath,
                command.Arguments,
                command.WorkingDirectory,
                timeout: command.Timeout,
                onOutput: line =>
                {
                    output.Add((line.IsStandardError ? "! " : string.Empty) + line.Text);
                    AddLog(line.Text);
                },
                cancellationToken: _lifetime.Token);

            _lastPreflightAt = DateTimeOffset.Now;
            _lastPreflightSucceeded = result.Succeeded;
            var tail = output.TakeLast(40).ToList();
            PreflightText = string.Join('\n', tail);
            if (PreflightText.Length == 0)
            {
                PreflightText = result.Started
                    ? $"The backend check finished with exit code {result.ExitCode} but printed nothing."
                    : "The backend could not start: " + result.StartFailure;
            }
            PreflightText = $"Backend exit code: {result.ExitCode}\n\n" + PreflightText;
            GameText = result.Succeeded
                ? "Preflight completed. Review the reported checks below."
                : "Preflight reported problems; review the output below.";
            SetStage(result.Succeeded ? "Preflight completed" : "Preflight failed");
        }
        finally
        {
            Exit();
        }
    }

    public async Task InstallSelectedAsync()
    {
        if (SelectedVersion is null)
        {
            AddLog("No release is selected.");
            return;
        }
        if (!TryEnter(nameof(InstallSelectedAsync), out var busyReason))
        {
            AddLog(busyReason);
            return;
        }

        var token = SelectedVersion.Token;
        var display = SelectedVersion.DisplayLabel;
        try
        {
            var backend = _backendState;
            if (backend is null || backend.ValidationStatus != BackendValidationStatus.Validated)
            {
                AddLog("Cannot install: the backend checkout is not valid.");
                return;
            }

            // The token must come from the validated live catalogue, never an ordinal.
            var entry = _catalogue?.FindByToken(token);
            if (entry is null || !CatalogueParser.IsValidToken(token))
            {
                AddLog($"Refusing to install '{token}': it is not in the validated catalogue.");
                return;
            }

            SetStage("Starting the Linux installer");
            var operationId = Guid.NewGuid().ToString("N");
            var command = _adapter.CreateInstall(backend, token, force: IsInstalled, refreshProton: false);
            var logPath = _logger.CreateOperationLog(operationId, command, backend.CommitHash);
            AddLog($"{display} → {command.QuotedCommandLine}");

            var record = new InstallerOperationRecord
            {
                OperationId = operationId,
                Kind = command.OperationKind,
                RequestedToken = token,
                RequestedDisplayName = display,
                LogPath = logPath,
                BackendCommit = backend.CommitHash,
                OldVersion = InstalledText,
            };

            var result = await _runner.RunAsync(
                command.ExecutablePath,
                command.Arguments,
                command.WorkingDirectory,
                environmentOverrides: command.EnvironmentOverrides,
                timeout: command.Timeout,
                onOutput: line =>
                {
                    _logger.Append(logPath, line);
                    AddLog((line.IsStandardError ? "! " : string.Empty) + line.Text);
                },
                cancellationToken: _lifetime.Token);

            var installed = _backend.ReadInstalledVersion();
            _logger.Complete(logPath, result, installed);
            record.ExitCode = result.ExitCode;
            record.FinishedAt = DateTimeOffset.Now;
            record.NewVersion = installed;
            record.Result = result.Succeeded ? "Succeeded" : "Failed";
            _logger.RecordOperation(record);

            if (!result.Succeeded)
            {
                SetStage($"Install finished with exit code {result.ExitCode}");
                return;
            }

            // Real postconditions, not optimism.
            var desktopStatus = ReconcileDesktopIntegration(backend);
            RefreshLocalState();
            _settings.SelectedToken = token;
            _settingsStore.Save(_settings);

            SetStage(desktopStatus.State == DesktopIntegrationState.Installed
                ? "Installed · WiVRn desktop entry installed"
                : "Installed · desktop integration needs attention");
            AddLog($"Backend reported: {installed ?? "unknown version"}");
            RaiseChanged();
        }
        finally
        {
            Exit();
        }
    }

    public async Task UninstallAsync()
    {
        if (!TryEnter(nameof(UninstallAsync), out _))
        {
            return;
        }
        try
        {
            if (_backendState is null || _backendState.ValidationStatus != BackendValidationStatus.Validated)
            {
                AddLog("Cannot uninstall: the backend checkout is not valid.");
                return;
            }
            SetStage("Removing the mod with the backend");
            var command = _adapter.CreateUninstall(_backendState);
            var operationId = Guid.NewGuid().ToString("N");
            var logPath = _logger.CreateOperationLog(operationId, command, _backendState.CommitHash);

            var result = await _runner.RunAsync(
                command.ExecutablePath, command.Arguments, command.WorkingDirectory,
                timeout: command.Timeout,
                onOutput: line =>
                {
                    _logger.Append(logPath, line);
                    AddLog((line.IsStandardError ? "! " : string.Empty) + line.Text);
                },
                cancellationToken: _lifetime.Token);

            _logger.Complete(logPath, result, _backend.ReadInstalledVersion());
            RefreshLocalState();
            SetStage(result.Succeeded ? "Uninstalled" : $"Uninstall failed with exit code {result.ExitCode}");
        }
        finally
        {
            Exit();
        }
    }

    public void LaunchVr()
    {
        var result = _launcher.Launch();
        AddLog(result.Message);
        SetStage(result.Started ? "VR launch requested" : "Launch failed");
        RefreshLocalState();
    }

    public async Task RepairDesktopAsync()
    {
        var backend = _backendState;
        if (backend is null || !File.Exists(backend.DesktopTemplatePath))
        {
            AddLog("The backend desktop template is not available; cannot repair.");
            return;
        }

        var status = _launcher.LauncherExists
            ? _desktop.Install(File.ReadAllText(backend.DesktopTemplatePath))
            : _desktop.Repair();

        _desktopStatus = status;
        DesktopText = DescribeDesktop(status);
        SetStage(status.State == DesktopIntegrationState.Installed
            ? "WiVRn desktop entry installed and validated"
            : "Desktop integration still needs attention");
        await Task.CompletedTask;
        RaiseChanged();
    }

    /// <summary>
    /// Rebuilds the game entry from the backend's canonical template. Unlike
    /// repair, this does not accept the content of an existing entry.
    /// </summary>
    public async Task ReinstallGameDesktopEntryAsync()
    {
        var backend = _backendState;
        if (backend is null || !File.Exists(backend.DesktopTemplatePath))
        {
            AddLog("The backend desktop template is not available; cannot reinstall the game entry.");
            return;
        }

        var status = _desktop.Install(File.ReadAllText(backend.DesktopTemplatePath));
        _desktopStatus = status;
        DesktopText = DescribeDesktop(status);
        SetStage(status.State == DesktopIntegrationState.Installed
            ? "Game desktop entry reinstalled and validated"
            : "Game desktop entry still needs attention");
        await Task.CompletedTask;
        RaiseChanged();
    }

    /// <summary>Reports whether a shortcut operation can run without interrupting a session.</summary>
    public bool CanModifySteamNow(out string? reason)
    {
        reason = null;
        if (_steamInstallation is null)
        {
            reason = Loc.Get("steamNotDetected");
            return false;
        }
        if (_steamProcess.IsGameSessionRunning())
        {
            reason = Loc.Get("steamGameRunning");
            return false;
        }
        var runtime = _runtime.Inspect();
        if (runtime.SteamVrRunning || runtime.WiVrnRunning || runtime.MonadoRunning)
        {
            reason = Loc.Get("vrSessionRunning");
            return false;
        }
        return true;
    }

    public string GetLatestLogText()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            var latest = Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                return Loc.Get("noLogs");
            }
            var lines = File.ReadAllLines(latest);
            return string.Join('\n', lines.TakeLast(400))
                + "\n\n" + Loc.Format("logFile", latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Loc.Format("logReadFailed", ex.Message);
        }
    }

    public void RestoreDefaults()
    {
        // Preferences only. Channel, Steam account and integration choices are
        // selection state, not preferences, and are preserved.
        _settings = new AppSettings
        {
            SelectedToken = _settings.SelectedToken,
            SelectedSteamInstallationId = _settings.SelectedSteamInstallationId,
            SelectedSteamAccountId = _settings.SelectedSteamAccountId,
        };
        SavePreferences();
        AddLog("Application preferences restored to defaults; no files or integrations were removed.");
        RaiseChanged();
    }

    public async Task<SteamTransactionResult?> AddOrUpdateSteamAsync(bool allowRestart)
    {
        if (_steamInstallation is null || _steamAccount is null)
        {
            AddLog("No Steam installation or account was selected.");
            return null;
        }
        if (!_launcher.LauncherExists)
        {
            AddLog("The tf2vr launcher is missing; install the mod before adding a Steam shortcut.");
            return null;
        }
        if (!CanModifySteamNow(out var blockReason))
        {
            AddLog(blockReason ?? "The Steam shortcut cannot be changed while a session is active.");
            SteamText = blockReason ?? "The Steam shortcut cannot be changed while a session is active.";
            SetStage("Steam shortcut not changed");
            return null;
        }

        SetStage("Updating the Steam shortcut");
        var result = await _steamManager.AddOrUpdateAsync(
            _steamInstallation, _steamAccount, _launcher.LauncherPath, allowRestart, _lifetime.Token);

        foreach (var line in result.Transaction.Timeline.TakeLast(6))
        {
            AddLog(line);
        }
        AddLog(result.Message);
        SteamText = result.Message ?? "Steam shortcut updated.";
        if (result.ShortcutVerified || result.Shortcut is ShortcutOutcome.AlreadyCorrect or ShortcutOutcome.Removed)
        {
            _settings.LastSteamIntegrationAt = DateTimeOffset.Now;
            _settings.LastSteamIntegrationResult = result.Message;
            SavePreferences();
        }
        SetStage(result.Restart == RestartOutcome.Verified
            ? "Steam shortcut updated and Steam reopened"
            : result.Shortcut == ShortcutOutcome.Failed ? "Steam shortcut failed" : "Steam shortcut updated");
        RefreshLocalState();
        return result;
    }

    public async Task RemoveSteamShortcutAsync(bool allowRestart)
    {
        if (_steamInstallation is null || _steamAccount is null)
        {
            AddLog("No Steam installation or account was selected.");
            return;
        }
        if (!CanModifySteamNow(out var blockReason))
        {
            AddLog(blockReason ?? "The Steam shortcut cannot be changed while a session is active.");
            SteamText = blockReason ?? "The Steam shortcut cannot be changed while a session is active.";
            SetStage("Steam shortcut not changed");
            return;
        }
        var result = await _steamManager.RemoveAsync(
            _steamInstallation, _steamAccount, _launcher.LauncherPath, allowRestart, _lifetime.Token);
        AddLog(result.Message);
        SteamText = result.Message ?? "Steam shortcut removed.";
        if (result.Shortcut is ShortcutOutcome.Removed or ShortcutOutcome.AlreadyCorrect)
        {
            _settings.LastSteamIntegrationAt = DateTimeOffset.Now;
            _settings.LastSteamIntegrationResult = result.Message;
            SavePreferences();
        }
        RefreshLocalState();
    }

    public async Task UpdateBackendAsync()
    {
        if (!TryEnter(nameof(UpdateBackendAsync), out _))
        {
            return;
        }
        try
        {
            _backendUpdateInProgress = true;
            SetStage("Updating the tf2vr-linux checkout");
            BackendUpdateStatusText = Loc.Get("backendUpdateRunning");
            RaiseChanged();
            var update = await _backend.UpdateAsync(_lifetime.Token);
            AddLog(update.Message);
            _backendState = update.Installation;
            BackendText = update.Installation.ValidationStatus == BackendValidationStatus.Validated
                ? $"tf2vr-linux {Short(update.Installation.CommitHash)} · origin verified"
                : update.Message;
            BackendUpdateStatusText = update.Message;
            if (update.Succeeded)
            {
                _settings.LastBackendUpdateAt = DateTimeOffset.Now;
                _settings.LastBackendUpdateResult = "Updated to " + Short(update.Installation.CommitHash);
                SavePreferences();
                _backendUpdateCheck = new BackendUpdateCheck
                {
                    OriginVerified = true,
                    WorkingTreeClean = true,
                    CurrentCommit = update.Installation.CommitHash,
                    RemoteCommit = update.Installation.CommitHash,
                    UpdateAvailable = false,
                    CheckedAt = DateTimeOffset.Now,
                };
                await RefreshCatalogueAsync();
                RefreshLocalState();
            }
            SetStage(update.Succeeded ? "Backend update complete" : "Backend update failed");
        }
        finally
        {
            _backendUpdateInProgress = false;
            Exit();
        }
    }

    /// <summary>Persists the exact backend token, never the dropdown ordinal.</summary>
    public void SelectVersion(VersionOption? option)
    {
        if (option is null || option.Token == SelectedVersion?.Token)
        {
            return;
        }
        SelectedVersion = option;
        _settings.SelectedToken = option.Token;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (IOException ex)
        {
            AddLog("Could not save the selection: " + ex.Message);
        }
        UpdatePrimaryAction();
        RaiseChanged();
    }

    public void SetRoundedCorners(bool enabled)
    {
        if (_settings.RoundedCorners == enabled)
        {
            return;
        }
        _settings.RoundedCorners = enabled;
        SavePreferences();
        RaiseChanged();
    }

    public void SetLanguage(string language)
    {
        if (string.Equals(_settings.Language, language, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _settings.Language = string.Equals(language, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        SavePreferences();
        RaiseChanged();
    }

    public void SetAutoCheckForAppUpdates(bool enabled)
    {
        if (_settings.CheckForAppUpdates == enabled)
        {
            return;
        }
        _settings.CheckForAppUpdates = enabled;
        SavePreferences();
        RaiseChanged();
    }

    public void SetUpdateBackendAutomatically(bool enabled)
    {
        if (_settings.UpdateBackendAutomatically == enabled)
        {
            return;
        }
        _settings.UpdateBackendAutomatically = enabled;
        SavePreferences();
        RaiseChanged();
    }

    public void SetConfirmSteamRestart(bool enabled)
    {
        if (_settings.ConfirmSteamRestart == enabled)
        {
            return;
        }
        _settings.ConfirmSteamRestart = enabled;
        SavePreferences();
        RaiseChanged();
    }

    public async Task CheckBackendUpdateAsync()
    {
        if (_checkingBackendUpdate)
        {
            return;
        }
        _checkingBackendUpdate = true;
        BackendUpdateStatusText = Loc.Get("checkingBackendUpdates");
        RaiseChanged();
        try
        {
            var check = await _backend.CheckForUpdateAsync(_lifetime.Token);
            _backendUpdateCheck = check;
            if (check.Error is not null)
            {
                BackendUpdateStatusText = Loc.Format("backendCheckFailed", check.Error);
                AddLog("Backend update check failed: " + check.Error);
            }
            else if (check.UpdateAvailable)
            {
                BackendUpdateStatusText = Loc.Format("backendUpdateAvailable", Short(check.RemoteCommit));
            }
            else
            {
                BackendUpdateStatusText = Loc.Get("backendUpToDate");
            }
            if (check.Error is null && !check.WorkingTreeClean)
            {
                BackendUpdateStatusText += " · " + Loc.Get("backendWorkingTreeDirty");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            BackendUpdateStatusText = Loc.Format("backendCheckFailed", ex.Message);
            AddLog("Backend update check failed: " + ex.Message);
        }
        finally
        {
            _checkingBackendUpdate = false;
            RaiseChanged();
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_checkingUpdates)
        {
            return;
        }
        _checkingUpdates = true;
        UpdateStatusText = Loc.Get("checkingUpdates");
        RaiseChanged();
        try
        {
            var result = await _releaseChecker.CheckAsync(CurrentVersion, _lifetime.Token);
            if (result.Error is not null)
            {
                LatestReleaseUrl = null;
                _latestAppTag = null;
                UpdateAvailable = false;
                UpdateStatusText = Loc.Format("updateCheckFailed", result.Error);
                AddLog("Update check failed: " + result.Error);
            }
            else if (result.UpdateAvailable)
            {
                LatestReleaseUrl = result.ReleaseUrl;
                _latestAppTag = result.LatestTag;
                UpdateAvailable = true;
                UpdateStatusText = Loc.Format("updateAvailable", result.LatestTag);
            }
            else
            {
                LatestReleaseUrl = result.ReleaseUrl;
                _latestAppTag = result.LatestTag ?? "v" + CurrentVersion.ToString(3);
                UpdateAvailable = false;
                UpdateStatusText = Loc.Format("upToDate", result.LatestTag ?? "v" + CurrentVersion.ToString(3));
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing; the update check is not worth reporting.
        }
        finally
        {
            _checkingUpdates = false;
            RaiseChanged();
        }
    }

    private void SavePreferences()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog("Could not save settings: " + ex.Message);
        }
    }

    public void RefreshLocalState()
    {
        _backendPaths = ReadPathsEnv(_paths.BackendPathsFile);
        _installedVersionRaw = _backend.ReadInstalledVersion();
        _installedToken = _backend.ReadSelectionToken();
        _lastOperation = _logger.ReadHistory().FirstOrDefault();
        var version = _installedVersionRaw;
        IsInstalled = !string.IsNullOrWhiteSpace(version) || _launcher.LauncherExists;
        InstalledText = version is not null
            ? Loc.Format("backendState", version)
            : (_launcher.LauncherExists ? Loc.Get("launcherVersionUnknown") : Loc.Get("notInstalled"));

        CanLaunch = _launcher.LauncherExists && _launcher.HasBackendConfig;
        CanUninstall = _backendState?.ValidationStatus == BackendValidationStatus.Validated && IsInstalled;
        CanInstall = _backendState?.ValidationStatus == BackendValidationStatus.Validated && Versions.Count > 0;

        GameText = _launcher.LauncherExists
            ? "tf2vr launcher found at " + _launcher.LauncherPath
            : "No tf2vr launcher yet. Run the backend check to confirm Titanfall 2 and Proton are present.";

        var desktop = _desktop.GetStatus();
        _desktopStatus = desktop;
        DesktopText = DescribeDesktop(desktop);

        DetectSteam();
        var runtime = _runtime.Inspect();
        _runtimeInfo = runtime;
        RuntimeText = runtime.Summary
            + (runtime.WiVrnRunning ? " · WiVRn running" : string.Empty)
            + (runtime.MonadoRunning ? " · Monado running" : string.Empty)
            + (runtime.SteamVrRunning ? " · SteamVR running" : string.Empty)
            + (runtime.ParseWarning is null ? string.Empty : " · " + runtime.ParseWarning);

        foreach (var pending in _steamManager.PendingRecoveries())
        {
            AddLog("An earlier Steam transaction still owes a restart (" + pending.TransactionId + ").");
        }

        UpdatePrimaryAction();
        RaiseChanged();
    }

    private void DetectSteam()
    {
        var installations = _steamLocator.DetectInstallations();
        _steamInstallation = installations.FirstOrDefault(i => i.InstallationId == _settings.SelectedSteamInstallationId)
            ?? installations.FirstOrDefault();

        if (_steamInstallation is null)
        {
            _steamAccount = null;
            _steamShortcutStatus = null;
            SteamText = "Steam: no native or Flatpak installation was detected.";
            return;
        }

        var accounts = _steamLocator.DetectAccounts(_steamInstallation);
        _steamAccount = accounts.FirstOrDefault(a => a.AccountId == _settings.SelectedSteamAccountId)
            ?? _steamLocator.SuggestAccount(accounts);

        if (_steamAccount is null)
        {
            _steamShortcutStatus = null;
            SteamText = $"Steam found at {_steamInstallation.InstallationRoot}, but no account userdata directory exists yet.";
            return;
        }

        var status = _steamManager.Inspect(_steamAccount, _launcher.LauncherPath);
        _steamShortcutStatus = status;
        SteamText = $"{_steamInstallation.InstallationType} Steam · account {_steamAccount.AccountId} · "
            + (status.State switch
            {
                ShortcutInstallState.Installed => "Titanfall 2 VR shortcut installed",
                ShortcutInstallState.ConflictingEntry => "conflicting shortcut entry",
                _ => "shortcut not installed",
            })
            + (_steamProcess.IsClientRunning(_steamInstallation) ? " · Steam running" : " · Steam closed");
        SteamText += status.Detail is null ? string.Empty : " · " + status.Detail;
    }

    private DesktopIntegrationStatus ReconcileDesktopIntegration(BackendInstallation backend)
    {
        try
        {
            if (!File.Exists(backend.DesktopTemplatePath))
            {
                return _desktop.GetStatus();
            }
            return _desktop.Install(File.ReadAllText(backend.DesktopTemplatePath));
        }
        catch (IOException ex)
        {
            AddLog("Desktop integration failed: " + ex.Message);
            return _desktop.GetStatus();
        }
    }

    private static string DescribeDesktop(DesktopIntegrationStatus status) => status.State switch
    {
        DesktopIntegrationState.Installed => Loc.Format("desktopEntryInstalled", status.DesktopFilePath),
        DesktopIntegrationState.NotInstalled => Loc.Get("desktopEntryNotInstalled"),
        DesktopIntegrationState.CategoryMissing => Loc.Get("desktopEntryCategoryMissing"),
        DesktopIntegrationState.LauncherMissing => Loc.Get("desktopEntryLauncherMissing"),
        DesktopIntegrationState.DuplicateEntries => Loc.Get("desktopEntryDuplicate"),
        DesktopIntegrationState.InvalidFile => Loc.Get("desktopEntryInvalid"),
        _ => status.Detail ?? status.State.ToString(),
    };

    private void UpdatePrimaryAction()
    {
        PrimaryActionLabel = IsInstalled ? Loc.Get("updateReinstall") : Loc.Get("install");
        if (_catalogue is not null && SelectedVersion is not null && IsInstalled)
        {
            var installedToken = _backend.ReadSelectionToken();
            if (installedToken is not null && installedToken != SelectedVersion.Token)
            {
                PrimaryActionLabel = Loc.Get("switchVersion");
            }
        }
    }

    private bool TryEnter(string operation, out string reason)
    {
        if (Busy)
        {
            reason = "Another operation is already running (" + StageText + ").";
            return false;
        }
        if (!_operationLock.Wait(0))
        {
            reason = "Another operation is already running.";
            return false;
        }
        Busy = true;
        reason = string.Empty;
        RaiseChanged();
        return true;
    }

    private void Exit()
    {
        Busy = false;
        _operationLock.Release();
        RaiseChanged();
    }

    private void SetStage(string stage)
    {
        StageText = stage;
        if (stage is not ("Ready" or "Starting"))
        {
            AddLog(stage);
        }
        RaiseChanged();
    }

    public void AddLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogLines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            while (LogLines.Count > 500)
            {
                LogLines.RemoveAt(0);
            }
        });
    }

    private string BuildRuntimeDetails()
    {
        var runtime = _runtimeInfo ?? _runtime.Inspect();
        var builder = new StringBuilder();
        builder.AppendLine("OpenXR runtime: " + (runtime.RuntimeName ?? "none registered"));
        builder.AppendLine("Runtime path: " + (runtime.RuntimePath ?? "not reported"));
        builder.AppendLine("Active runtime file: " + runtime.ActiveRuntimeJsonPath + (runtime.JsonFound ? " (found)" : " (missing)"));
        builder.AppendLine("SteamVR: " + (runtime.SteamVrAvailable ? runtime.SteamVrPath : "not detected")
            + (runtime.SteamVrRunning ? " · running" : string.Empty));
        builder.AppendLine("WiVRn: " + (runtime.WiVrnAvailable ? "detected" : "not detected")
            + (runtime.WiVrnRunning ? " · running" : string.Empty));
        builder.AppendLine("Monado: " + (runtime.MonadoAvailable ? "detected" : "not detected")
            + (runtime.MonadoRunning ? " · running" : string.Empty));
        builder.AppendLine("OpenVR bridge: " + (runtime.OpenVrBridgePath is null
            ? "not found"
            : (runtime.OpenVrBridgeKind ?? "detected") + " · " + runtime.OpenVrBridgePath));
        builder.AppendLine("OpenVR paths file: " + (runtime.OpenVrPathsFile ?? "not available"));
        builder.AppendLine("tf2vr launcher: " + (_launcher.LauncherExists ? _launcher.LauncherPath : "not installed"));
        builder.AppendLine("Desktop entry: " + DesktopEntryStatusText);
        if (runtime.ParseWarning is not null)
        {
            builder.AppendLine("Warning: " + runtime.ParseWarning);
        }
        builder.AppendLine();
        builder.AppendLine("This panel is informational. The active OpenXR runtime is not changed by this application.");
        return builder.ToString();
    }

    private string BuildDiagnosticsReport()
    {
        var version = _backend.ReadInstalledVersion();
        var runtime = _runtimeInfo ?? _runtime.Inspect();
        var builder = new StringBuilder();
        builder.AppendLine("CircuitLord's VR Mod Installer — diagnostic report");
        builder.AppendLine("Generated: " + DateTimeOffset.Now.ToString("O"));
        builder.AppendLine("Application version: " + CurrentAppVersion);
        builder.AppendLine("Platform: Linux · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        builder.AppendLine("Language: " + _settings.Language);
        builder.AppendLine("Backend commit: " + (_backendState?.CommitHash ?? "not available"));
        builder.AppendLine("Backend checkout: " + _paths.BackendCheckout);
        builder.AppendLine("Backend origin verified: " + (_backendState?.VerifiedOrigin == true ? "yes" : "no"));
        builder.AppendLine("Backend working tree: " + BackendWorkingTreeText);
        builder.AppendLine("Installed mod version: " + (version ?? "not installed"));
        builder.AppendLine("Selected channel: " + (SelectedVersion?.DisplayLabel ?? _settings.SelectedToken ?? "not selected"));
        builder.AppendLine("Game installation: " + PathValue("GAME_DIR"));
        builder.AppendLine("VR prefix: " + PathValue("PREFIX_DIR"));
        builder.AppendLine("Launcher: " + (_launcher.LauncherExists ? _launcher.LauncherPath : "not installed"));
        builder.AppendLine("OpenXR runtime: " + (runtime.RuntimeName ?? "none registered"));
        builder.AppendLine("SteamVR: " + (runtime.SteamVrAvailable ? "detected" : "not detected")
            + (runtime.SteamVrRunning ? " · running" : string.Empty));
        builder.AppendLine("WiVRn: " + (runtime.WiVrnAvailable ? "detected" : "not detected")
            + (runtime.WiVrnRunning ? " · running" : string.Empty));
        builder.AppendLine("Monado: " + (runtime.MonadoAvailable ? "detected" : "not detected")
            + (runtime.MonadoRunning ? " · running" : string.Empty));
        builder.AppendLine("OpenVR bridge: " + (runtime.OpenVrBridgePath ?? "not found"));
        builder.AppendLine("Steam: " + (_steamInstallation is null
            ? "not detected"
            : _steamInstallation.InstallationType + " · " + _steamInstallation.InstallationRoot));
        builder.AppendLine("Steam account: " + (_steamAccount is null ? "not detected" : "detected (account id withheld)"));
        builder.AppendLine("Titanfall 2 VR shortcut: " + SteamShortcutStatusText);
        builder.AppendLine("Shortcut executable: " + SteamShortcutExecutableText);
        builder.AppendLine("WiVRn desktop entry: " + DesktopEntryStatusText);
        builder.AppendLine("Last preflight: " + LastPreflightText);
        builder.AppendLine();
        builder.AppendLine("Credentials, authentication tokens and Steam account identifiers are not included.");
        return builder.ToString();
    }

    private string PathValue(string key) =>
        _backendPaths.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "not recorded";

    private static IReadOnlyDictionary<string, string> ReadPathsEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path))
            {
                return values;
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                var key = line[..separator];
                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                {
                    value = value[1..^1].Replace("'\\''", "'");
                }
                else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }
                values[key] = value.Replace("\\ ", " ");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return values;
    }

    private void RaiseChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private static string Short(string? commit) => commit is { Length: >= 7 } ? commit[..7] : commit ?? "unknown";

    private static string? ModVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var fields = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var mod = fields.FirstOrDefault(f => f.StartsWith("mod=", StringComparison.Ordinal));
        return mod is null ? raw : mod["mod=".Length..];
    }
}
