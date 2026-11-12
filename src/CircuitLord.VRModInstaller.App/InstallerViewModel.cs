using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    public string AppVersion => "1.0.0 · Linux";

    public bool RoundedCorners => _settings.RoundedCorners;
    public string Language => _settings.Language;
    public bool AutoCheckForAppUpdates => _settings.CheckForAppUpdates;
    public bool UpdateBackendAutomatically => _settings.UpdateBackendAutomatically;
    public string? LatestReleaseUrl { get; private set; }
    public bool UpdateAvailable { get; private set; }
    public string UpdateStatusText { get; private set; } = Loc.Get("updatesNotChecked");

    public string BackendText { get; private set; } = "Checking the Linux backend...";
    public string CatalogueText { get; private set; } = "Loading version catalogue...";
    public string InstalledText { get; private set; } = "Not installed";
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

        DesktopText = DescribeDesktop(status);
        SetStage(status.State == DesktopIntegrationState.Installed
            ? "WiVRn desktop entry installed and validated"
            : "Desktop integration still needs attention");
        await Task.CompletedTask;
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

        SetStage("Updating the Steam shortcut");
        var result = await _steamManager.AddOrUpdateAsync(
            _steamInstallation, _steamAccount, _launcher.LauncherPath, allowRestart, _lifetime.Token);

        foreach (var line in result.Transaction.Timeline.TakeLast(6))
        {
            AddLog(line);
        }
        AddLog(result.Message);
        SteamText = result.Message ?? "Steam shortcut updated.";
        SetStage(result.Restart == RestartOutcome.Verified
            ? "Steam shortcut updated and Steam reopened"
            : result.Shortcut == ShortcutOutcome.Failed ? "Steam shortcut failed" : "Steam shortcut updated");
        RaiseChanged();
        return result;
    }

    public async Task RemoveSteamShortcutAsync(bool allowRestart)
    {
        if (_steamInstallation is null || _steamAccount is null)
        {
            AddLog("No Steam installation or account was selected.");
            return;
        }
        var result = await _steamManager.RemoveAsync(
            _steamInstallation, _steamAccount, _launcher.LauncherPath, allowRestart, _lifetime.Token);
        AddLog(result.Message);
        SteamText = result.Message ?? "Steam shortcut removed.";
        RaiseChanged();
    }

    public async Task UpdateBackendAsync()
    {
        if (!TryEnter(nameof(UpdateBackendAsync), out _))
        {
            return;
        }
        try
        {
            SetStage("Updating the tf2vr-linux checkout");
            var update = await _backend.UpdateAsync(_lifetime.Token);
            AddLog(update.Message);
            _backendState = update.Installation;
            BackendText = update.Installation.ValidationStatus == BackendValidationStatus.Validated
                ? $"tf2vr-linux {Short(update.Installation.CommitHash)} · origin verified"
                : update.Message;
            if (update.Succeeded)
            {
                await RefreshCatalogueAsync();
                RefreshLocalState();
            }
            SetStage(update.Succeeded ? "Backend update complete" : "Backend update failed");
        }
        finally
        {
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
                UpdateAvailable = false;
                UpdateStatusText = Loc.Format("updateCheckFailed", result.Error);
                AddLog("Update check failed: " + result.Error);
            }
            else if (result.UpdateAvailable)
            {
                LatestReleaseUrl = result.ReleaseUrl;
                UpdateAvailable = true;
                UpdateStatusText = Loc.Format("updateAvailable", result.LatestTag);
            }
            else
            {
                LatestReleaseUrl = result.ReleaseUrl;
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
        var version = _backend.ReadInstalledVersion();
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
        DesktopText = DescribeDesktop(desktop);

        DetectSteam();
        var runtime = _runtime.Inspect();
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
            SteamText = "Steam: no native or Flatpak installation was detected.";
            return;
        }

        var accounts = _steamLocator.DetectAccounts(_steamInstallation);
        _steamAccount = accounts.FirstOrDefault(a => a.AccountId == _settings.SelectedSteamAccountId)
            ?? _steamLocator.SuggestAccount(accounts);

        if (_steamAccount is null)
        {
            SteamText = $"Steam found at {_steamInstallation.InstallationRoot}, but no account userdata directory exists yet.";
            return;
        }

        var status = _steamManager.Inspect(_steamAccount, _launcher.LauncherPath);
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
        DesktopIntegrationState.Installed => "WiVRn entry ok · Categories=Game;X-WiVRn-VR; · " + status.DesktopFilePath,
        DesktopIntegrationState.NotInstalled => "WiVRn desktop entry: not installed.",
        DesktopIntegrationState.CategoryMissing => "WiVRn desktop entry is missing the required category.",
        DesktopIntegrationState.LauncherMissing => "Desktop entry points at a missing launcher.",
        DesktopIntegrationState.DuplicateEntries => "More than one Titanfall 2 VR desktop entry exists.",
        DesktopIntegrationState.InvalidFile => "desktop-file-validate rejected the entry.",
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

    private void RaiseChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private static string Short(string? commit) => commit is { Length: >= 7 } ? commit[..7] : commit ?? "unknown";
}
