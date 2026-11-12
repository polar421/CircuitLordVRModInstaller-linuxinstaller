using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core;
using CircuitLord.VRModInstaller.Core.Appearance;
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
    public string GroupKey { get; init; } = "pinned";
    public string GroupTitle { get; init; } = "Pinned";
    public string KindLabel { get; init; } = "Fixed version";
    public string Description { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public bool IsSelected { get; init; }
    public bool IsUpToDate { get; init; }
    public string VersionText => Version ?? "—";
    public override string ToString() => DisplayLabel;
}

public sealed class VersionGroup
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public required IReadOnlyList<VersionOption> Options { get; init; }
    public bool HasOptions => Options.Count > 0;
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
    private readonly AppUpdateService _appUpdater;
    private static readonly Version CurrentVersion =
        typeof(InstallerViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
    private static readonly string BuildInformationalVersion =
        typeof(InstallerViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? string.Empty;
    private static readonly string BuildVersion = BuildInformationalVersion.Length == 0
        ? CurrentVersion.ToString(3)
        : BuildInformationalVersion.Split('+', 2)[0];
    private static readonly bool BuildIsBeta = BuildVersion.Contains('-');
    private int _updateCheckGeneration;
    private bool _appUpdateInProgress;
    private string _updateStatusKey = "updatesNotChecked";
    private object?[] _updateStatusArgs = Array.Empty<object?>();
    private string? _backendUpdateStatusKey;
    private object?[] _backendUpdateStatusArgs = Array.Empty<object?>();
    private string _stageKey = "stageStarting";
    private object?[] _stageArgs = Array.Empty<object?>();
    private bool _checkingBackendUpdate;
    private bool _backendUpdateInProgress;
    private string? _latestAppTag;
    private ReleaseCheckResult? _latestAppRelease;
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
    private InstallerOperationRecord? _lastSuccessfulInstall;
    private ManifestSnapshot? _manifestSnapshot;
    private ReleaseNotesSelection _changelogSelection = ReleaseNotesSelection.None;
    private bool _historyUnavailable;

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
        _appUpdater = new AppUpdateService(_paths);
        _settings = _settingsStore.Load();
    }

    public event EventHandler? Changed;
    /// <summary>Raised after the detached updater helper is ready and the window should close.</summary>
    public event EventHandler? RestartRequested;
    public ObservableCollection<string> LogLines { get; } = new();

    public string AppTitle => "CircuitLord's VR Mod Installer";
    public string CurrentAppVersion => BuildVersion;
    public string AppVersion => CurrentAppVersion + " · Linux · " + Loc.Get("unofficialLabel");
    public string PlatformText => "Linux · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription
        + " · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

    public bool RoundedCorners => _settings.RoundedCorners;
    public string Language => _settings.Language;
    public string ThemeSetting => _settings.Theme;
    public int UiScalePercent => _settings.UiScale;
    public string MotionSetting => _settings.MotionPreference;
    public bool AutoCheckForAppUpdates => _settings.CheckForAppUpdates;
    public string UpdateChannelSetting => _settings.UpdateChannel;
    public bool IsBetaUpdateChannel => ReleaseChannelPreference.IncludesPrereleases(
        _settings.UpdateChannel, BuildIsBeta);
    public bool UpdateBackendAutomatically => _settings.UpdateBackendAutomatically;
    public bool ConfirmSteamRestart => _settings.ConfirmSteamRestart;
    public string? LatestReleaseUrl { get; private set; }
    public bool UpdateAvailable { get; private set; }
    public bool AppUpdateInProgress => _appUpdateInProgress;
    public bool CanInstallAppUpdate => UpdateAvailable
        && !_appUpdateInProgress
        && !Busy
        && _latestAppRelease?.CanAutoUpdate == true
        && _appUpdater.IsInstalledCopy();
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

    public string BackendText { get; private set; } = Loc.Get("backendChecking");
    public string CatalogueText { get; private set; } = Loc.Get("catalogueLoading");
    public string InstalledText { get; private set; } = Loc.Get("notInstalled");
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
    public string SelectedVersionTitle => SelectedVersion?.DisplayLabel ?? Loc.Get("chooseRelease");
    public string SelectedVersionMeta => SelectedVersion is null
        ? Loc.Get("chooseRelease")
        : SelectedVersion.KindLabel + " · " + SelectedVersion.VersionText;
    public string SelectedChannelText => SelectedVersion is null
        ? Loc.Get("channelUnknown")
        : GroupTitleFor(SelectedVersion.GroupKey);
    public string ModUpdateStateText => !IsInstalled
        ? Loc.Get("notInstalled")
        : StableUpdateAvailable
            ? Loc.Get("updateAvailableShort")
            : SelectedVersion is not null && !IsSameRelease(SelectedVersion)
                ? Loc.Get("selectedReleasePending")
                : UpToDateStatusText;
    public string InstalledCardStatusText => !IsInstalled
        ? Loc.Get("notInstalled")
        : StableUpdateAvailable
            ? Loc.Get("updateAvailableShort")
            : SelectedVersion is not null && !IsSameRelease(SelectedVersion)
                ? Loc.Get("selectedReleasePending")
                : UpToDateStatusText;

    private string UpToDateStatusText => SelectedVersion?.GroupKey switch
    {
        "beta" => Loc.Format("upToDateOnChannel", Loc.Get("betaReleases")),
        "stable" => Loc.Format("upToDateOnChannel", Loc.Get("stableReleases")),
        "pinned" => Loc.Get("upToDateOnSelectedRelease"),
        _ => IsOnBeta
            ? Loc.Format("upToDateOnChannel", Loc.Get("betaReleases"))
            : _installedToken == "latest"
                ? Loc.Format("upToDateOnChannel", Loc.Get("stableReleases"))
                : Loc.Get("upToDateShort"),
    };
    public string LatestStableBadgeText => Loc.Get("installed");
    public string LatestBetaBadgeText => Loc.Get("installed");
    public bool LatestStableBadgeVisible => IsInstalled && !IsOnBeta
        && IsSameVersion(ModVersion(_installedVersionRaw), LatestStableVersionText);
    public bool LatestBetaBadgeVisible => IsInstalled && IsOnBeta
        && IsSameVersion(ModVersion(_installedVersionRaw), LatestBetaVersionText);
    public string InstallationHistoryText
    {
        get
        {
            if (_historyUnavailable)
            {
                return Loc.Get("historyUnavailable");
            }
            if (!IsInstalled)
            {
                return Loc.Get("noInstallRecord");
            }
            if (IsVerifiedInstallRecord())
            {
                return Loc.Format("historyInstalledByApp", OperationDateText(_lastSuccessfulInstall));
            }
            return _lastOperation is null
                ? Loc.Get("historyExistingInstall")
                : Loc.Get("historyExistingWithActivity");
        }
    }
    public string LastOperationDetailText => _lastOperation is null
        ? string.Empty
        : Loc.Format(
            "historyLastAttempt",
            OperationDateText(_lastOperation),
            _lastOperation.Result switch
            {
                "Succeeded" => Loc.Get("operationSucceeded"),
                "Running" => Loc.Get("operationRunning"),
                _ => Loc.Get("operationFailed"),
            });
    public string ChangelogPreview { get; private set; } = Loc.Get("changelogUnavailable");
    public string ChangelogFullText { get; private set; } = Loc.Get("changelogUnavailable");
    public bool ChangelogCanExpand { get; private set; }
    public string ChangelogChannelText => _changelogSelection.Source switch
    {
        ReleaseNotesSource.SelectedRelease => SelectedVersion is null
            ? Loc.Get("stableLabel")
            : GroupTitleFor(SelectedVersion.GroupKey),
        ReleaseNotesSource.InstalledRelease => string.IsNullOrWhiteSpace(_installedToken)
            ? Loc.Get("installedNotes")
            : Loc.Get("installedNotes") + " · " + InstalledChannelText,
        ReleaseNotesSource.StableManifest => Loc.Get("stableNotes"),
        _ => Loc.Get("notesUnavailableLabel"),
    };
    public string ChangelogUpdatedText => _manifestSnapshot is null
        ? Loc.Get("notLoaded")
        : Loc.Format("lastRefreshed", _manifestSnapshot.RetrievedAt.ToLocalTime().ToString("g"));
    public string ChangelogSourceText => _manifestSnapshot is null
        ? Loc.Get("notLoaded")
        : _manifestSnapshot.IsFromCache ? Loc.Get("cachedData") : Loc.Get("liveData");
    public bool CanReinstall => IsInstalled && CanInstall;
    public bool CanUpdateSelected => IsInstalled
        && CanInstall
        && SelectedVersion is not null
        && !IsSameRelease(SelectedVersion);
    public bool CanPrimaryAction => CanInstall && !Busy;
    public string PrimaryActionHint { get; private set; } = Loc.Get("primaryHintInstall");
    public string GameText { get; private set; } = Loc.Get("gameStateUnknown");
    public string PreflightText { get; private set; } = Loc.Get("preflightHint");
    public string DesktopText { get; private set; } = Loc.Get("desktopNotChecked");
    public string SteamText { get; private set; } = Loc.Get("steamNotChecked");
    public string RuntimeText { get; private set; } = Loc.Get("runtimeNotChecked");
    public string ManifestText { get; private set; } = string.Empty;
    public string StageText { get; private set; } = Loc.Get("stageStarting");
    public string PrimaryActionLabel { get; private set; } = Loc.Get("install");
    public string BigWalkText { get; private set; } = Loc.Get("bigWalkUnavailable");

    public bool Busy { get; private set; }
    public bool CanInstall { get; private set; }
    public bool CanLaunch { get; private set; }
    public bool CanUninstall { get; private set; }
    public bool IsInstalled { get; private set; }
    public bool IsSteamRunning => _steamInstallation is not null && _steamProcess.IsClientRunning(_steamInstallation);
    public bool HasSteamAccount => _steamAccount is not null;
    public bool BackendVerified => _backendState?.ValidationStatus == BackendValidationStatus.Validated;
    public bool GameDetected => _backendPaths.TryGetValue("GAME_DIR", out var gameDir)
        && !string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir);
    public bool OpenXrDetected => _runtimeInfo is not null
        && (_runtimeInfo.RuntimeName is not null || _runtimeInfo.SteamVrAvailable || _runtimeInfo.WiVrnAvailable || _runtimeInfo.MonadoAvailable);
    public bool DesktopEntryInstalled => _desktopStatus?.State == DesktopIntegrationState.Installed;
    public bool StableUpdateAvailable => IsInstalled
        && IsNewer(LatestStableVersionText, ModVersion(_installedVersionRaw));
    public VersionOption? SelectedVersion { get; set; }
    public List<VersionOption> Versions { get; private set; } = new();
    public IReadOnlyList<VersionGroup> VersionGroups => BuildVersionGroups();

    public async Task InitializeAsync()
    {
        SetStage("stageCheckingBackend");

        _backendState = await _backend.EnsureAsync(_lifetime.Token);
        BackendText = BuildBackendText(_backendState);

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
        else if (_catalogue is null)
        {
            CatalogueText = Loc.Get("catalogueUnavailable");
        }

        RefreshLocalState();
        SetStage("stageReady");
        RaiseChanged();

        if (_settings.CheckForAppUpdates)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    private async Task LoadManifestAsync()
    {
        var snapshot = await _manifest.GetAsync(forceRefresh: false, _lifetime.Token);
        _manifestSnapshot = snapshot;
        RefreshChangelog();
        ManifestText = BuildManifestText(snapshot);
        RaiseChanged();
    }

    private static string BuildBackendText(BackendInstallation? state)
    {
        if (state is null)
        {
            return Loc.Get("backendNotAvailable");
        }
        if (state.ValidationStatus == BackendValidationStatus.Validated)
        {
            return $"tf2vr-linux {Short(state.CommitHash)} · " + Loc.Get("backendOriginVerified");
        }
        return state.ValidationStatus switch
        {
            BackendValidationStatus.CloneFailed => Loc.Get("backendCloneFailed"),
            BackendValidationStatus.OriginMismatch => Loc.Get("backendOriginMismatch"),
            BackendValidationStatus.Incomplete => Loc.Get("backendIncomplete"),
            _ => Loc.Get("backendNotAvailable"),
        };
    }

    private string BuildCatalogueText(VersionCatalogue? catalogue)
    {
        if (_backendState is null || _backendState.ValidationStatus != BackendValidationStatus.Validated)
        {
            return Loc.Get("catalogueUnavailable");
        }
        if (catalogue is null)
        {
            return Loc.Get("catalogueLoading");
        }
        if (catalogue.Entries.Count == 0)
        {
            return catalogue.Diagnostic ?? Loc.Get("catalogueNoVersions");
        }
        return Loc.Format(
                   "catalogueSummary",
                   catalogue.IsFromCache ? Loc.Get("catalogueCached") : Loc.Get("catalogueLive"),
                   catalogue.Entries.Count)
               + (catalogue.Diagnostic is null ? string.Empty : " · " + catalogue.Diagnostic);
    }

    private static string BuildManifestText(ManifestSnapshot? snapshot)
    {
        if (snapshot?.Titanfall2Vr is null)
        {
            return snapshot?.ValidationWarning ?? Loc.Get("changelogUnavailable");
        }
        var product = snapshot.Titanfall2Vr;
        var text = (snapshot.IsFromCache ? Loc.Get("cachedData") : Loc.Get("liveData"))
            + $" · stable {product.Version}"
            + (product.Channels.Count > 0 ? $" · beta {product.Channels[0].Version}" : string.Empty);
        return snapshot.ValidationWarning is null
            ? text
            : text + " · " + snapshot.ValidationWarning;
    }

    public async Task RefreshCatalogueAsync()
    {
        if (_backendState is null || _backendState.ValidationStatus != BackendValidationStatus.Validated)
        {
            CatalogueText = Loc.Get("catalogueUnavailable");
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

        CatalogueText = BuildCatalogueText(catalogue);

        RefreshChangelog();
        UpdatePrimaryAction();
        RaiseChanged();
    }

    private void ApplyCatalogue(VersionCatalogue catalogue)
    {
        _catalogue = catalogue;
        Versions = catalogue.Entries.Select(CreateVersionOption).ToList();

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
                PreflightText = Loc.Get("preflightNothingRun");
                return;
            }

            SetStage("stagePreflight");
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
                    ? Loc.Format("preflightNoOutput", result.ExitCode)
                    : Loc.Format("preflightCouldNotStart", result.StartFailure);
            }
            PreflightText = Loc.Format("preflightExitCode", result.ExitCode) + "\n\n" + PreflightText;
            GameText = result.Succeeded
                ? Loc.Get("preflightReviewChecks")
                : Loc.Get("preflightReviewProblems");
            SetStage(result.Succeeded ? "stagePreflightCompleted" : "stagePreflightFailed");
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

            SetStage("stageStartingInstaller");
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
                SetStage("stageInstallFinished", result.ExitCode);
                return;
            }

            // Real postconditions, not optimism.
            var desktopStatus = ReconcileDesktopIntegration(backend);
            RefreshLocalState();
            _settings.SelectedToken = token;
            _settingsStore.Save(_settings);

            SetStage(desktopStatus.State == DesktopIntegrationState.Installed
                ? "stageInstalledDesktop"
                : "stageInstalledDesktopAttention");
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
            SetStage("stageUninstalling");
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
            SetStage(result.Succeeded ? "stageUninstalled" : "stageUninstallFailed", result.Succeeded ? Array.Empty<object?>() : new object?[] { result.ExitCode });
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
        SetStage(result.Started ? "stageLaunchRequested" : "stageLaunchFailed");
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
            ? "desktopInstalledValidated"
            : "desktopNeedsAttention");
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
            ? "desktopGameReinstalled"
            : "desktopGameNeedsAttention");
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
        // Preferences only. The backend release choice, Steam account and
        // integration choices are selection state, not preferences, and are preserved.
        _settings = new AppSettings
        {
            SelectedToken = _settings.SelectedToken,
            SelectedSteamInstallationId = _settings.SelectedSteamInstallationId,
            SelectedSteamAccountId = _settings.SelectedSteamAccountId,
        };
        _updateCheckGeneration++;
        _latestAppRelease = null;
        _latestAppTag = null;
        LatestReleaseUrl = null;
        UpdateAvailable = false;
        SetUpdateStatus("updatesNotChecked");
        AppearanceManager.SetTheme(ThemePreference.Dark);
        AppearanceManager.SetScale(UiScale.DefaultPercent);
        AppearanceManager.SetMotion(MotionPreference.System);
        Loc.Use("en");
        RelocalizeAfterLanguageChange();
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
            SteamText = blockReason ?? Loc.Get("steamSessionActive");
            SetStage("stageSteamNotChanged");
            return null;
        }

        SetStage("stageSteamUpdating");
        var result = await _steamManager.AddOrUpdateAsync(
            _steamInstallation, _steamAccount, _launcher.LauncherPath, allowRestart, _lifetime.Token);

        foreach (var line in result.Transaction.Timeline.TakeLast(6))
        {
            AddLog(line);
        }
        AddLog(result.Message);
        SteamText = BuildSteamResultText(result);
        if (result.ShortcutVerified || result.Shortcut is ShortcutOutcome.AlreadyCorrect or ShortcutOutcome.Removed)
        {
            _settings.LastSteamIntegrationAt = DateTimeOffset.Now;
            _settings.LastSteamIntegrationResult = result.Message;
            SavePreferences();
        }
        SetStage(result.Restart == RestartOutcome.Verified
            ? "stageSteamUpdated"
            : result.Shortcut == ShortcutOutcome.Failed ? "stageSteamFailed" : "stageSteamUpdated");
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
            SteamText = blockReason ?? Loc.Get("steamSessionActive");
            SetStage("stageSteamNotChanged");
            return;
        }
        var result = await _steamManager.RemoveAsync(
            _steamInstallation, _steamAccount, _launcher.LauncherPath, allowRestart, _lifetime.Token);
        AddLog(result.Message);
        SteamText = BuildSteamResultText(result);
        if (result.Shortcut is ShortcutOutcome.Removed or ShortcutOutcome.AlreadyCorrect)
        {
            _settings.LastSteamIntegrationAt = DateTimeOffset.Now;
            _settings.LastSteamIntegrationResult = result.Message;
            SavePreferences();
        }
        RefreshLocalState();
    }

    private static string BuildSteamResultText(SteamTransactionResult result)
    {
        var localized = result.Shortcut switch
        {
            ShortcutOutcome.Added => Loc.Get("steamShortcutAdded"),
            ShortcutOutcome.Updated => Loc.Get("steamShortcutUpdated"),
            ShortcutOutcome.Removed => Loc.Get("steamShortcutRemoved"),
            ShortcutOutcome.AlreadyCorrect => Loc.Get("steamShortcutAlreadyCorrect"),
            ShortcutOutcome.Failed => Loc.Get("steamShortcutFailed"),
            _ => null,
        };

        if (localized is null)
        {
            return result.Message ?? Loc.Get("steamShortcutUpdated");
        }

        // The short UI status is localized; failure detail stays as the backend/git
        // diagnostic it is. Timelines and logs still contain the original message.
        return result.Shortcut == ShortcutOutcome.Failed && !string.IsNullOrWhiteSpace(result.Message)
            ? localized + " " + result.Message
            : localized;
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
            SetStage("stageBackendUpdating");
            SetBackendUpdateStatus("backendUpdateRunning");
            RaiseChanged();
            var update = await _backend.UpdateAsync(_lifetime.Token);
            AddLog(update.Message);
            _backendState = update.Installation;
            BackendText = BuildBackendText(update.Installation);
            SetBackendUpdateStatusRaw(update.Message);
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
            SetStage(update.Succeeded ? "stageBackendUpdateComplete" : "stageBackendUpdateFailed");
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
        RefreshChangelog();
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

    public void SetTheme(string theme)
    {
        var preference = ThemePreferenceNames.Parse(theme);
        var setting = preference.ToSetting();
        if (string.Equals(_settings.Theme, setting, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Theme = setting;
        AppearanceManager.SetTheme(preference);
        SavePreferences();
        RaiseChanged();
    }

    public void SetUiScale(int percent)
    {
        var clamped = Core.Appearance.UiScale.ClampPercent(percent);
        if (_settings.UiScale == clamped)
        {
            return;
        }

        _settings.UiScale = clamped;
        AppearanceManager.SetScale(clamped);
        SavePreferences();
        RaiseChanged();
    }

    public void SetMotion(string motion)
    {
        var preference = MotionPreferenceNames.Parse(motion);
        var setting = preference.ToSetting();
        if (string.Equals(_settings.MotionPreference, setting, StringComparison.Ordinal))
        {
            return;
        }

        _settings.MotionPreference = setting;
        AppearanceManager.SetMotion(preference);
        SavePreferences();
        RaiseChanged();
    }

    public void SetLanguage(string language)
    {
        var normalized = Loc.NormalizeLanguage(language);
        if (string.Equals(_settings.Language, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Language = normalized;
        Loc.Use(normalized);
        RelocalizeAfterLanguageChange();
        SavePreferences();
        RaiseChanged();
    }

    /// <summary>
    /// Most list/status values are materialized strings rather than bindings.
    /// Rebuild them from the cached state so switching language updates the
    /// installer screen, not just labels declared in XAML.
    /// </summary>
    private void RelocalizeAfterLanguageChange()
    {
        RelocalizeVersions();
        BackendText = BuildBackendText(_backendState);
        CatalogueText = BuildCatalogueText(_catalogue);
        ManifestText = BuildManifestText(_manifestSnapshot);
        BigWalkText = Loc.Get("bigWalkUnavailable");
        if (_lastPreflightAt is null)
        {
            PreflightText = Loc.Get("preflightHint");
        }
        RefreshLocalState();
        RelocalizeStage();
        RelocalizeUpdateStatuses();
    }

    private void RelocalizeVersions()
    {
        if (_catalogue is null)
        {
            return;
        }

        var selectedToken = SelectedVersion?.Token;
        Versions = _catalogue.Entries.Select(CreateVersionOption).ToList();
        SelectedVersion = Versions.FirstOrDefault(v => v.Token == selectedToken)
            ?? Versions.FirstOrDefault();
    }

    private VersionOption CreateVersionOption(VersionCatalogueEntry entry) => new()
    {
        DisplayLabel = LocalizedDisplayLabel(entry),
        Token = entry.BackendToken,
        Version = entry.ResolvedVersion,
        GroupKey = GroupKeyFor(entry),
        KindLabel = entry.IsPinnedRelease ? Loc.Get("fixedVersion") : Loc.Get("channel"),
        // Moving channels already have a localized group hint. Pinned
        // descriptions are backend-provided technical text.
        Description = entry.IsPinnedRelease ? entry.Description ?? string.Empty : string.Empty,
    };

    private static string LocalizedDisplayLabel(VersionCatalogueEntry entry)
    {
        if (entry.BackendToken == "latest")
        {
            return Loc.Get("channelStable");
        }
        if (entry.BackendToken == "beta")
        {
            return Loc.Get("betaReleases");
        }
        return entry.DisplayName;
    }

    private void RelocalizeStage()
        => StageText = Loc.Format(_stageKey, _stageArgs);

    private void RelocalizeUpdateStatuses()
    {
        UpdateStatusText = Loc.Format(_updateStatusKey, _updateStatusArgs);
        if (_backendUpdateStatusKey is not null)
        {
            BackendUpdateStatusText = Loc.Format(_backendUpdateStatusKey, _backendUpdateStatusArgs);
        }
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

    public void SetUpdateChannel(string channel)
    {
        var normalized = ReleaseChannelPreference.Normalize(channel);
        if (_settings.UpdateChannel == normalized)
        {
            return;
        }
        _settings.UpdateChannel = normalized;
        // Drop the previous channel's result and ignore any check already in
        // flight before the new check runs, so Stable cannot briefly offer a beta.
        _updateCheckGeneration++;
        _latestAppRelease = null;
        _latestAppTag = null;
        LatestReleaseUrl = null;
        UpdateAvailable = false;
        SetUpdateStatus("updatesNotChecked");
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
        SetBackendUpdateStatus("checkingBackendUpdates");
        RaiseChanged();
        try
        {
            var check = await _backend.CheckForUpdateAsync(_lifetime.Token);
            _backendUpdateCheck = check;
            if (check.Error is not null)
            {
                SetBackendUpdateStatus("backendCheckFailed", check.Error);
                AddLog("Backend update check failed: " + check.Error);
            }
            else if (check.UpdateAvailable)
            {
                SetBackendUpdateStatus("backendUpdateAvailable", Short(check.RemoteCommit));
            }
            else
            {
                SetBackendUpdateStatus("backendUpToDate");
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
            SetBackendUpdateStatus("backendCheckFailed", ex.Message);
            AddLog("Backend update check failed: " + ex.Message);
        }
        finally
        {
            _checkingBackendUpdate = false;
            RaiseChanged();
        }
    }

    public async Task InstallAppUpdateAsync()
    {
        if (_appUpdateInProgress)
        {
            return;
        }
        if (_latestAppRelease is not { UpdateAvailable: true } release)
        {
            SetUpdateStatus("updatesNotChecked");
            RaiseChanged();
            return;
        }
        if (!release.CanAutoUpdate)
        {
            SetUpdateStatus("appUpdatePackageMissing");
            AddLog("This release does not include the Linux update archive.");
            RaiseChanged();
            return;
        }
        if (!_appUpdater.IsInstalledCopy())
        {
            SetUpdateStatus("appUpdateInstalledOnly");
            AddLog("Automatic installation is only available from the installed application.");
            RaiseChanged();
            return;
        }
        if (Busy)
        {
            SetUpdateStatus("appUpdateBusy");
            RaiseChanged();
            return;
        }

        _appUpdateInProgress = true;
        // Keep mod installs and helper updates from starting underneath the
        // updater, which will close the app before the helper replaces it.
        Busy = true;
        SetUpdateStatus("appUpdateDownloading");
        AddLog("Downloading installer update " + (release.LatestTag ?? string.Empty) + ".");
        RaiseChanged();
        try
        {
            var package = await _appUpdater.PrepareAsync(
                release,
                progress =>
                {
                    if (progress.Fraction is { } fraction)
                    {
                        SetUpdateStatus("appUpdateDownloadingProgress", (int)Math.Round(fraction * 100));
                    }
                    else
                    {
                        SetUpdateStatus("appUpdateDownloading");
                    }
                    RaiseChanged();
                },
                _lifetime.Token);

            SetUpdateStatus("appUpdateRestarting", release.LatestTag ?? "");
            AddLog("Update verified. Restarting into " + (release.LatestTag ?? "the new version") + ".");
            RaiseChanged();
            _appUpdater.StartApply(package, Environment.ProcessId);
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("appUpdateCanceled");
        }
        catch (Exception ex)
        {
            SetUpdateStatus("appUpdateFailed", ex.Message);
            AddLog("Installer update failed: " + ex.Message);
        }
        finally
        {
            _appUpdateInProgress = false;
            Busy = false;
            RaiseChanged();
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        var generation = ++_updateCheckGeneration;
        SetUpdateStatus("checkingUpdates");
        RaiseChanged();
        try
        {
            var includePrereleases = ReleaseChannelPreference.IncludesPrereleases(
                _settings.UpdateChannel, BuildIsBeta);
            var result = await _releaseChecker.CheckAsync(
                CurrentAppVersion, includePrereleases, _lifetime.Token);
            if (generation != _updateCheckGeneration)
            {
                // The channel changed while this request was running; its
                // result belongs to the old channel and must not be applied.
                return;
            }
            _latestAppRelease = result;
            if (result.Error is not null)
            {
                LatestReleaseUrl = null;
                _latestAppTag = null;
                UpdateAvailable = false;
                SetUpdateStatus("updateCheckFailed", result.Error);
                AddLog("Update check failed: " + result.Error);
            }
            else if (result.UpdateAvailable)
            {
                LatestReleaseUrl = result.ReleaseUrl;
                _latestAppTag = result.LatestTag;
                UpdateAvailable = true;
                SetUpdateStatus(result.CanAutoUpdate ? "updateAvailable" : "updateAvailableManual", result.LatestTag);
                if (_settings.CheckForAppUpdates && result.CanAutoUpdate)
                {
                    await InstallAppUpdateAsync();
                }
            }
            else
            {
                var currentTag = "v" + CurrentAppVersion;
                LatestReleaseUrl = result.ReleaseUrl;
                _latestAppTag = result.LatestTag ?? currentTag;
                UpdateAvailable = false;
                SetUpdateStatus("upToDate", result.LatestTag ?? currentTag);
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing; the update check is not worth reporting.
        }
        finally
        {
            if (generation == _updateCheckGeneration)
            {
                RaiseChanged();
            }
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
        try
        {
            var history = _logger.ReadHistory();
            _lastOperation = history.FirstOrDefault();
            _lastSuccessfulInstall = history.FirstOrDefault(IsSuccessfulInstallRecord);
            _historyUnavailable = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastOperation = null;
            _lastSuccessfulInstall = null;
            _historyUnavailable = true;
        }
        var version = _installedVersionRaw;
        IsInstalled = !string.IsNullOrWhiteSpace(version) || _launcher.LauncherExists;
        InstalledText = version is not null
            ? Loc.Format("installedStatus", ModVersion(version) ?? Loc.Get("versionUnknown"))
            : (_launcher.LauncherExists ? Loc.Get("launcherVersionUnknown") : Loc.Get("notInstalled"));

        CanLaunch = _launcher.LauncherExists && _launcher.HasBackendConfig;
        CanUninstall = _backendState?.ValidationStatus == BackendValidationStatus.Validated && IsInstalled;
        CanInstall = _backendState?.ValidationStatus == BackendValidationStatus.Validated && Versions.Count > 0;

        GameText = _launcher.LauncherExists
            ? Loc.Format("gameLauncherFound", _launcher.LauncherPath)
            : Loc.Get("gameLauncherMissingHint");

        var desktop = _desktop.GetStatus();
        _desktopStatus = desktop;
        DesktopText = DescribeDesktop(desktop);

        DetectSteam();
        var runtime = _runtime.Inspect();
        _runtimeInfo = runtime;
        RuntimeText = DescribeRuntime(runtime)
            + (runtime.WiVrnRunning ? " · WiVRn " + Loc.Get("running") : string.Empty)
            + (runtime.MonadoRunning ? " · Monado " + Loc.Get("running") : string.Empty)
            + (runtime.SteamVrRunning ? " · SteamVR " + Loc.Get("running") : string.Empty)
            + (runtime.ParseWarning is null ? string.Empty : " · " + runtime.ParseWarning);

        foreach (var pending in _steamManager.PendingRecoveries())
        {
            AddLog("An earlier Steam transaction still owes a restart (" + pending.TransactionId + ").");
        }

        RefreshChangelog();
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
            SteamText = Loc.Get("steamNoInstallation");
            return;
        }

        var accounts = _steamLocator.DetectAccounts(_steamInstallation);
        _steamAccount = accounts.FirstOrDefault(a => a.AccountId == _settings.SelectedSteamAccountId)
            ?? _steamLocator.SuggestAccount(accounts);

        if (_steamAccount is null)
        {
            _steamShortcutStatus = null;
            SteamText = Loc.Format("steamNoAccount", _steamInstallation.InstallationRoot);
            return;
        }

        var status = _steamManager.Inspect(_steamAccount, _launcher.LauncherPath);
        _steamShortcutStatus = status;
        var shortcutState = status.State switch
        {
            ShortcutInstallState.Installed => Loc.Get("steamShortcutStateInstalled"),
            ShortcutInstallState.ConflictingEntry => Loc.Get("steamShortcutStateConflicting"),
            _ => Loc.Get("steamShortcutStateMissing"),
        };
        SteamText = Loc.Format(
            "steamDetails",
            _steamInstallation.InstallationType,
            _steamAccount.AccountId,
            shortcutState + (_steamProcess.IsClientRunning(_steamInstallation)
                ? " · " + Loc.Get("steamRunning")
                : " · " + Loc.Get("steamClosed")));
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

    private static string DescribeRuntime(OpenXrRuntimeInfo runtime)
        => runtime.JsonFound && runtime.RuntimeName is not null
            ? Loc.Get("activeOpenXrRuntime") + ": " + runtime.RuntimeName
            : Loc.Get("noRuntimeRegistered");

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

    private IReadOnlyList<VersionGroup> BuildVersionGroups()
    {
        var definitions = new (string Key, string Title, string Hint)[]
        {
            ("stable", Loc.Get("stableReleases"), Loc.Get("stableReleasesHint")),
            ("beta", Loc.Get("betaReleases"), Loc.Get("betaReleasesHint")),
            ("pinned", Loc.Get("pinnedReleases"), Loc.Get("pinnedReleasesHint")),
        };
        var selectedToken = SelectedVersion?.Token;
        var groups = new List<VersionGroup>();
        foreach (var definition in definitions)
        {
            var options = Versions
                .Where(v => v.GroupKey == definition.Key)
                .Select(v =>
                {
                    var installed = IsVersionInstalled(v);
                    return new VersionOption
                    {
                        DisplayLabel = v.DisplayLabel,
                        Token = v.Token,
                        Version = v.Version,
                        GroupKey = definition.Key,
                        GroupTitle = definition.Title,
                        KindLabel = Loc.Get(definition.Key == "pinned" ? "fixedVersion" : "channel"),
                        Description = v.Description,
                        IsInstalled = installed,
                        IsSelected = string.Equals(selectedToken, v.Token, StringComparison.Ordinal),
                        IsUpToDate = installed && definition.Key == "stable" && !StableUpdateAvailable,
                    };
                })
                .ToList();
            if (options.Count > 0)
            {
                groups.Add(new VersionGroup
                {
                    Key = definition.Key,
                    Title = definition.Title,
                    Hint = definition.Hint,
                    Options = options,
                });
            }
        }
        return groups;
    }

    private static string GroupTitleFor(string key) => key switch
    {
        "stable" => Loc.Get("stableReleases"),
        "beta" => Loc.Get("betaReleases"),
        _ => Loc.Get("pinnedReleases"),
    };

    private static string GroupKeyFor(VersionCatalogueEntry entry)
    {
        if (entry.IsPinnedRelease)
        {
            return "pinned";
        }
        if (entry.BackendToken == "beta" || entry.ChannelId == "beta")
        {
            return "beta";
        }
        return "stable";
    }

    private bool IsVersionInstalled(VersionOption option)
    {
        var installedVersion = ModVersion(_installedVersionRaw);
        if (!string.IsNullOrWhiteSpace(option.Version) && !string.IsNullOrWhiteSpace(installedVersion))
        {
            return IsSameVersion(option.Version, installedVersion);
        }
        return !string.IsNullOrWhiteSpace(_installedToken)
            && string.Equals(option.Token, _installedToken, StringComparison.Ordinal);
    }

    private bool IsSameRelease(VersionOption selected)
    {
        var installedVersion = ModVersion(_installedVersionRaw);
        if (!string.IsNullOrWhiteSpace(selected.Version) && !string.IsNullOrWhiteSpace(installedVersion))
        {
            return IsSameVersion(selected.Version, installedVersion);
        }
        return string.Equals(selected.Token, _installedToken, StringComparison.Ordinal);
    }

    private bool IsOnBeta => _installedToken == "beta"
        || (_installedToken?.StartsWith("beta-", StringComparison.Ordinal) ?? false);

    private static bool IsSuccessfulInstallRecord(InstallerOperationRecord record) =>
        string.Equals(record.Result, "Succeeded", StringComparison.Ordinal)
        && record.Kind is BackendOperationKind.Install or BackendOperationKind.Update or BackendOperationKind.Reinstall;

    private bool IsVerifiedInstallRecord()
    {
        if (_lastSuccessfulInstall is null)
        {
            return false;
        }
        var recorded = ModVersion(_lastSuccessfulInstall.NewVersion);
        var installed = ModVersion(_installedVersionRaw);
        // A stale record must not claim an installation this app did not perform.
        return string.IsNullOrWhiteSpace(recorded)
            || string.IsNullOrWhiteSpace(installed)
            || IsSameVersion(recorded, installed);
    }

    private static string OperationDateText(InstallerOperationRecord? record) =>
        (record?.FinishedAt ?? record?.StartedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("g");

    private static string FormatChangelog(IReadOnlyList<ManifestChange> changes, int maxEntries)
    {
        var selected = changes.Take(maxEntries).ToList();
        return selected.Count == 0
            ? Loc.Get("changelogUnavailable")
            : string.Join("\n\n", selected.Select(change =>
                "v" + change.Version
                + (change.Date is null ? string.Empty : "  ·  " + change.Date)
                + "\n" + change.Notes.Trim()));
    }

    private void RefreshChangelog()
    {
        var product = _manifestSnapshot?.Titanfall2Vr;
        _changelogSelection = product is null
            ? ReleaseNotesSelection.None
            : ReleaseNotesSelector.Select(
                product,
                SelectedVersion?.Version,
                ModVersion(_installedVersionRaw));

        ChangelogPreview = FormatChangelog(_changelogSelection.Changes, 2);
        ChangelogFullText = FormatChangelog(_changelogSelection.Changes, int.MaxValue);
        ChangelogCanExpand = _changelogSelection.Changes.Count > 2
            || ChangelogFullText.Length > 260
            || ChangelogFullText.Count(character => character == '\n') >= 4;
    }

    private void UpdatePrimaryAction()
    {
        if (!CanInstall)
        {
            PrimaryActionLabel = Loc.Get("install");
            PrimaryActionHint = Loc.Get("primaryUnavailable");
            return;
        }
        if (IsInstallationIncomplete)
        {
            PrimaryActionLabel = Loc.Get("repair");
            PrimaryActionHint = Loc.Get("primaryHintRepair");
            return;
        }
        var selected = SelectedVersion;
        if (!IsInstalled)
        {
            PrimaryActionLabel = selected?.GroupKey switch
            {
                "beta" => Loc.Get("installBeta"),
                "pinned" => Loc.Get("installSelectedVersion"),
                _ => Loc.Get("install"),
            };
            PrimaryActionHint = Loc.Get("primaryHintInstall");
            return;
        }
        if (selected is null)
        {
            PrimaryActionLabel = Loc.Get("install");
            PrimaryActionHint = Loc.Get("primaryHintInstall");
            return;
        }
        if (IsSameRelease(selected))
        {
            PrimaryActionLabel = Loc.Get("reinstall");
            PrimaryActionHint = Loc.Get("primaryHintReinstall");
            return;
        }
        if (selected.GroupKey == "beta")
        {
            PrimaryActionLabel = IsOnBeta ? Loc.Get("installBeta") : Loc.Get("switchToBeta");
            PrimaryActionHint = Loc.Get("primaryHintUpdate");
            return;
        }
        if (selected.GroupKey == "stable")
        {
            PrimaryActionLabel = IsOnBeta || !IsNewer(selected.Version, ModVersion(_installedVersionRaw))
                ? Loc.Get("switchToStable")
                : Loc.Get("update");
            PrimaryActionHint = Loc.Get("primaryHintUpdate");
            return;
        }
        PrimaryActionLabel = Loc.Get("installSelectedVersion");
        PrimaryActionHint = Loc.Get("primaryHintUpdate");
    }

    public bool IsInstallationIncomplete => IsInstalled
        && (!CanLaunch || string.IsNullOrWhiteSpace(ModVersion(_installedVersionRaw)));

    private static bool IsSameVersion(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || (Version.TryParse(left, out var leftVersion)
                && Version.TryParse(right, out var rightVersion)
                && leftVersion == rightVersion);
    }

    private static bool IsNewer(string? candidate, string? baseline) =>
        Version.TryParse(candidate, out var candidateVersion)
        && Version.TryParse(baseline, out var baselineVersion)
        && candidateVersion > baselineVersion;

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

    private void SetStage(string key, params object?[] args)
    {
        _stageKey = key;
        _stageArgs = args;
        StageText = Loc.Format(key, args);
        if (key is not ("stageReady" or "stageStarting"))
        {
            AddLog(StageText);
        }
        RaiseChanged();
    }

    private void SetBackendUpdateStatus(string key, params object?[] args)
    {
        _backendUpdateStatusKey = key;
        _backendUpdateStatusArgs = args;
        BackendUpdateStatusText = Loc.Format(key, args);
    }

    private void SetBackendUpdateStatusRaw(string text)
    {
        _backendUpdateStatusKey = null;
        _backendUpdateStatusArgs = Array.Empty<object?>();
        BackendUpdateStatusText = text;
    }

    private void SetUpdateStatus(string key, params object?[] args)
    {
        _updateStatusKey = key;
        _updateStatusArgs = args;
        UpdateStatusText = Loc.Format(key, args);
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
        builder.AppendLine(Loc.Get("activeOpenXrRuntime") + ": " + (runtime.RuntimeName ?? Loc.Get("noRuntimeRegistered")));
        builder.AppendLine(Loc.Get("runtimePath") + ": " + (runtime.RuntimePath ?? Loc.Get("notReported")));
        builder.AppendLine(Loc.Get("activeRuntimeFile") + ": " + runtime.ActiveRuntimeJsonPath
            + " (" + (runtime.JsonFound ? Loc.Get("found") : Loc.Get("missing")) + ")");
        builder.AppendLine(Loc.Get("steamVrAvailability") + ": " + (runtime.SteamVrAvailable ? runtime.SteamVrPath : Loc.Get("notDetected"))
            + (runtime.SteamVrRunning ? " · " + Loc.Get("running") : string.Empty));
        builder.AppendLine(Loc.Get("wivrnAvailability") + ": " + (runtime.WiVrnAvailable ? Loc.Get("detected") : Loc.Get("notDetected"))
            + (runtime.WiVrnRunning ? " · " + Loc.Get("running") : string.Empty));
        builder.AppendLine(Loc.Get("monadoAvailability") + ": " + (runtime.MonadoAvailable ? Loc.Get("detected") : Loc.Get("notDetected"))
            + (runtime.MonadoRunning ? " · " + Loc.Get("running") : string.Empty));
        builder.AppendLine(Loc.Get("openVrBridge") + ": " + (runtime.OpenVrBridgePath is null
            ? Loc.Get("notFound")
            : (runtime.OpenVrBridgeKind ?? Loc.Get("detected")) + " · " + runtime.OpenVrBridgePath));
        builder.AppendLine(Loc.Get("openVrPathsFile") + ": " + (runtime.OpenVrPathsFile ?? Loc.Get("notAvailable")));
        builder.AppendLine(Loc.Get("tf2vrLauncher") + ": " + (_launcher.LauncherExists ? _launcher.LauncherPath : Loc.Get("notInstalled")));
        builder.AppendLine(Loc.Get("desktopEntry") + ": " + DesktopEntryStatusText);
        if (runtime.ParseWarning is not null)
        {
            builder.AppendLine(Loc.Get("warning") + ": " + runtime.ParseWarning);
        }
        builder.AppendLine();
        builder.AppendLine(Loc.Get("runtimeInfoNote"));
        return builder.ToString();
    }

    private string BuildDiagnosticsReport()
    {
        var version = _backend.ReadInstalledVersion();
        var runtime = _runtimeInfo ?? _runtime.Inspect();
        var builder = new StringBuilder();
        builder.AppendLine(Loc.Get("diagnosticReportTitle"));
        builder.AppendLine(Loc.Get("generated") + ": " + DateTimeOffset.Now.ToString("O"));
        builder.AppendLine(Loc.Get("applicationVersion") + ": " + CurrentAppVersion);
        builder.AppendLine(Loc.Get("aboutPlatform") + ": Linux · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        builder.AppendLine(Loc.Get("language") + ": " + _settings.Language);
        builder.AppendLine(Loc.Get("backendCommit") + ": " + (_backendState?.CommitHash ?? Loc.Get("notAvailable")));
        builder.AppendLine(Loc.Get("backendCheckout") + ": " + _paths.BackendCheckout);
        builder.AppendLine(Loc.Get("backendOriginVerified") + ": " + (_backendState?.VerifiedOrigin == true ? Loc.Get("yes") : Loc.Get("no")));
        builder.AppendLine(Loc.Get("helperWorkingTree") + ": " + BackendWorkingTreeText);
        builder.AppendLine(Loc.Get("installedVersion") + ": " + (version ?? Loc.Get("notInstalled")));
        builder.AppendLine(Loc.Get("selectedChannel") + ": " + (SelectedVersion?.DisplayLabel ?? _settings.SelectedToken ?? Loc.Get("notSelected")));
        builder.AppendLine(Loc.Get("gameInstallation") + ": " + PathValue("GAME_DIR"));
        builder.AppendLine(Loc.Get("vrPrefix") + ": " + PathValue("PREFIX_DIR"));
        builder.AppendLine(Loc.Get("tf2vrLauncher") + ": " + (_launcher.LauncherExists ? _launcher.LauncherPath : Loc.Get("notInstalled")));
        builder.AppendLine(Loc.Get("activeOpenXrRuntime") + ": " + (runtime.RuntimeName ?? Loc.Get("noneRegistered")));
        builder.AppendLine(Loc.Get("steamVrAvailability") + ": " + (runtime.SteamVrAvailable ? Loc.Get("detected") : Loc.Get("notDetected"))
            + (runtime.SteamVrRunning ? " · " + Loc.Get("running") : string.Empty));
        builder.AppendLine(Loc.Get("wivrnAvailability") + ": " + (runtime.WiVrnAvailable ? Loc.Get("detected") : Loc.Get("notDetected"))
            + (runtime.WiVrnRunning ? " · " + Loc.Get("running") : string.Empty));
        builder.AppendLine(Loc.Get("monadoAvailability") + ": " + (runtime.MonadoAvailable ? Loc.Get("detected") : Loc.Get("notDetected"))
            + (runtime.MonadoRunning ? " · " + Loc.Get("running") : string.Empty));
        builder.AppendLine(Loc.Get("openVrBridge") + ": " + (runtime.OpenVrBridgePath ?? Loc.Get("notFound")));
        builder.AppendLine(Loc.Get("steamLabel") + ": " + (_steamInstallation is null
            ? Loc.Get("notDetected")
            : _steamInstallation.InstallationType + " · " + _steamInstallation.InstallationRoot));
        builder.AppendLine(Loc.Get("steamSelectedAccount") + ": " + (_steamAccount is null ? Loc.Get("notDetected") : Loc.Get("steamAccountDetected")));
        builder.AppendLine(Loc.Get("steamShortcutStatus") + ": " + SteamShortcutStatusText);
        builder.AppendLine(Loc.Get("steamShortcutExecutable") + ": " + SteamShortcutExecutableText);
        builder.AppendLine(Loc.Get("desktopEntry") + ": " + DesktopEntryStatusText);
        builder.AppendLine(Loc.Get("lastPreflight") + ": " + LastPreflightText);
        builder.AppendLine();
        builder.AppendLine(Loc.Get("diagnosticsPrivacy"));
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
