using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Navigation;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

public partial class MainWindow : Window
{
    // Same destination as the original Windows installer (MainWindow.xaml.cs).
    private const string SupportUrl = "https://ko-fi.com/circuitlord";

    private readonly InstallerViewModel _vm = new();
    private TaskCompletionSource<bool>? _confirmation;
    private bool _changelogExpanded;

    private double _gameScrollOffset;
    private double _installerScrollOffset;
    private ScrollViewer? _logScrollViewer;
    private bool _followLogs = true;
    private string _logFilter = string.Empty;
    private List<string> _visibleLogs = new();
    private bool _updatingDetailsExpander;
    private double _lastLogOffsetY;
    private bool _gamePageOpen;
    private bool _gameTransitionRunning;
    private bool _gameTransitionOpening;
    private CancellationTokenSource? _gameTransitionCts;
    private long _gameTransitionVersion;
    private bool _initializationComplete;
    private bool _setupWizardPending;
    private bool _setupWizardLeavingSettings;
    private bool _switchingTitanfallTab;
    private bool _releaseTabInitialized;
    private bool _historyTabInitialized;
    private bool _diagnosticsTabInitialized;

    public MainWindow()
    {
        InitializeComponent();
        // Return keyboard focus to the title-bar action after the closing
        // animation has removed the modal from the visual tree. When Settings
        // was only a stepping stone to the setup wizard, hand focus there.
        SettingsPanel.Closed += (_, _) =>
        {
            if (_setupWizardLeavingSettings)
            {
                _setupWizardLeavingSettings = false;
                ShowSetupWizard(force: true);
                return;
            }
            if (_gamePageOpen)
            {
                // Return to the destination the user was working in, not the
                // global title-bar action that opened Settings.
                InstallerTabs.FocusSelectedButton();
            }
            else
            {
                this.FindControl<Button>("SettingsButton")?.Focus();
            }
        };
        SettingsPanel.SetupWizardRequested += (_, _) =>
        {
            _setupWizardLeavingSettings = true;
            SettingsPanel.Close();
        };
        SetupWizardPanel.Closed += (_, _) =>
            this.FindControl<Button>("TitanfallCard")?.Focus();
        HistoryPanel.Closed += (_, _) =>
            this.FindControl<Button>("OpenHistoryButton")?.Focus();
        ReleaseExplorerPanel.Closed += (_, _) =>
            this.FindControl<Button>("BrowseReleasesButton")?.Focus();
        HistoryPanel.OverviewRequested += (_, _) => SelectTitanfallTab(TitanfallTab.Overview, focusTab: true);
        ReleaseExplorerPanel.OverviewRequested += (_, _) => SelectTitanfallTab(TitanfallTab.Overview, focusTab: true);
        DiagnosticsPanel.OpenLogDirectoryRequested += (_, _) =>
        {
            Directory.CreateDirectory(_vm.LogsDirectory);
            OpenPath(_vm.LogsDirectory);
        };
        DiagnosticsPanel.RepairSteamRequested += RepairSteamFromDiagnosticsAsync;
        // Resolve the placement target explicitly so the flyout is anchored to
        // the release selector instead of falling back to the window centre.
        VersionPopup.PlacementTarget = this.FindControl<Button>("VersionPickerButton");
        // Tunnel Escape so Settings closes even when focus is on a control that
        // consumes the bubbling key event (for example a dropdown or text box).
        AddHandler(KeyDownEvent, SettingsEscape_Handler, RoutingStrategies.Tunnel);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CircuitLordVRModInstaller/Assets/app-256.png")));
        ApplyCornerStyle(_vm.RoundedCorners);
        AppearanceManager.ScaleChanged += Appearance_Changed;
        AppearanceManager.MotionChanged += Appearance_Changed;
        AppearanceManager.Theme.Changed += Theme_Changed;
        SizeChanged += (_, _) =>
        {
            ApplyScale();
            if (_gameTransitionRunning)
            {
                FinishGameTransitionNow();
            }
        };
        Closed += (_, _) =>
        {
            HelpTip.CloseOpen();
            CancelGameTransition();
            AppearanceManager.ScaleChanged -= Appearance_Changed;
            AppearanceManager.MotionChanged -= Appearance_Changed;
            AppearanceManager.Theme.Changed -= Theme_Changed;
            _vm.LogLines.CollectionChanged -= LogLines_CollectionChanged;
            NotificationCenter.Close();
            DetachLogScrollViewer();
        };
        ApplyScale();
        ApplyMotionClasses();
        DataContext = _vm;
        ApplyDensityClass();
        ApplyRememberedDetailsState();
        ApplyLogFilter();
        NotificationCenter.Open(_vm);
        _vm.Changed += (_, _) => RefreshUi();
        // Auto-update starts a detached helper and then asks the window to close.
        // The helper waits for this process before swapping the app directory.
        _vm.RestartRequested += (_, _) => Close();
        Opened += async (_, _) =>
        {
            // Show the notice immediately while backend initialization proceeds.
            ShowAttributionNotice();
            await _vm.InitializeAsync();
            _initializationComplete = true;
            RefreshUi();
            if (_setupWizardPending)
            {
                _setupWizardPending = false;
                ShowSetupWizard();
            }
        };
    }

    private void Appearance_Changed(object? sender, EventArgs e)
    {
        if (_gameTransitionRunning)
        {
            // A scale or motion change invalidates measured coordinates. Settle
            // on the destination rather than leaving an overlay mid-flight.
            FinishGameTransitionNow();
        }

        ApplyScale();
        ApplyMotionClasses();
        if (!AppearanceManager.AnimationsEnabled)
        {
            ResetTitanfallTabTransition();
        }
    }

    private void ResetTitanfallTabTransition()
    {
        foreach (var target in new Control?[]
        {
            this.FindControl<ScrollViewer>("InstallerScroll"),
            ReleaseExplorerPanel,
            HistoryPanel,
            DiagnosticsPanel,
        })
        {
            if (target is null)
            {
                continue;
            }
            target.Transitions = null;
            target.Opacity = 1;
            target.RenderTransform = IdentityTransform;
        }
    }

    private void Theme_Changed(object? sender, EventArgs e)
    {
        // Brushes assigned from code-behind are not DynamicResource bindings;
        // re-apply them after the variant changes.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshUi();
            SetupWizardPanel.RefreshAppearance();
            HistoryPanel.RefreshAppearance();
            ReleaseExplorerPanel.RefreshAppearance();
            DiagnosticsPanel.RefreshAppearance();
            InstallerTabs.RefreshAppearance();
        });
    }

    private void ApplyMotionClasses()
        => AppearanceManager.ApplyMotionClasses(this);

    private void ApplyScale()
    {
        var factor = AppearanceManager.ScaleFactor;
        var root = this.FindControl<LayoutTransformControl>("RootScale");
        if (root is not null)
        {
            root.LayoutTransform = new ScaleTransform(factor, factor);
        }

        var popupScale = this.FindControl<LayoutTransformControl>("VersionPopupScale");
        if (popupScale is not null)
        {
            popupScale.LayoutTransform = new ScaleTransform(factor, factor);
        }
        NotificationCenter.ApplyScale();

        var popup = this.FindControl<Border>("VersionPopupPanel");
        if (popup is not null)
        {
            var availableWidth = Bounds.Width > 0 ? (Bounds.Width - 40) / factor : 520;
            popup.Width = Math.Clamp(availableWidth, 260, 520);
            var availableHeight = Bounds.Height > 0 ? (Bounds.Height - 80) / factor : 340;
            popup.MaxHeight = Math.Clamp(availableHeight, 220, 340);
        }
    }

    private void ShowAttributionNotice()
    {
        var overlay = this.FindControl<Border>("AttributionOverlay");
        if (overlay is null)
        {
            return;
        }
        overlay.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (overlay.IsVisible)
                {
                    this.FindControl<Button>("AttributionContinueButton")?.Focus();
                }
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void ContinueAttribution_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Border>("AttributionOverlay")!.IsVisible = false;
        if (!_vm.SetupWizardCompleted)
        {
            if (_initializationComplete)
            {
                ShowSetupWizard();
            }
            else
            {
                // The notice is shown while the backend starts. Opening the
                // wizard before that finishes would race its state refreshes.
                _setupWizardPending = true;
            }
            return;
        }
        this.FindControl<Button>("TitanfallCard")?.Focus();
    }

    private void ShowSetupWizard(bool force = false)
    {
        if (SetupWizardPanel.IsVisible || (!force && _vm.SetupWizardCompleted))
        {
            return;
        }
        SetupWizardPanel.Open(_vm);
    }

    private void OpenAttributionLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            OpenUrl(url);
        }
    }

    private void SettingsEscape_Handler(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        if (HelpTip.CloseOpen())
        {
            e.Handled = true;
            return;
        }
        var popup = this.FindControl<Popup>("VersionPopup");
        if (popup?.IsOpen == true)
        {
            popup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (NotificationCenter.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (SetupWizardPanel.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (SettingsPanel.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (HistoryPanel.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (ReleaseExplorerPanel.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (_gamePageOpen && InstallerTabs.SelectedTab != TitanfallTab.Overview)
        {
            SelectTitanfallTab(TitanfallTab.Overview, focusTab: true);
            e.Handled = true;
        }
    }

    private void RefreshUi()
    {
        this.FindControl<TextBlock>("InstalledVersionValue")!.Text = _vm.InstalledVersionText;
        this.FindControl<TextBlock>("InstalledVersionBadgeText")!.Text = _vm.InstalledCardStatusText;
        var installedBadge = this.FindControl<Border>("InstalledVersionBadge")!;
        installedBadge.IsVisible = _vm.IsInstalled;
        installedBadge.BorderBrush = BrushFor(_vm.StableUpdateAvailable ? "Amber" : "Green");
        this.FindControl<TextBlock>("InstalledVersionBadgeText")!.Foreground =
            BrushFor(_vm.StableUpdateAvailable ? "Amber" : "Green");
        var channelValue = this.FindControl<TextBlock>("ChannelValue")!;
        channelValue.Text = _vm.IsInstalled ? _vm.InstalledChannelText : Loc.Get("notInstalledHint");
        channelValue.IsVisible = true;

        this.FindControl<TextBlock>("HeroInstalledVersion")!.Text = _vm.InstalledVersionText;
        this.FindControl<TextBlock>("HeroSelectedChannel")!.Text = _vm.SelectedChannelText;
        this.FindControl<TextBlock>("HeroUpdateText")!.Text = _vm.ModUpdateStateText;
        this.FindControl<TextBlock>("HeroUpdateText")!.Foreground = BrushFor(
            !_vm.IsInstalled ? "TextDim" : _vm.StableUpdateAvailable ? "Amber" : "Green");
        this.FindControl<TextBlock>("HeroStatusText")!.Text = _vm.IsInstalled
            ? Loc.Get("overviewInstalled")
            : Loc.Get("overviewNotInstalled");
        this.FindControl<TextBlock>("LatestStableValue")!.Text = _vm.LatestStableVersionText;
        this.FindControl<TextBlock>("LatestBetaValue")!.Text = _vm.LatestBetaVersionText;
        this.FindControl<Border>("LatestStableBadge")!.IsVisible = _vm.LatestStableBadgeVisible;
        this.FindControl<TextBlock>("LatestStableBadgeText")!.Text = _vm.LatestStableBadgeText;
        this.FindControl<Border>("LatestBetaBadge")!.IsVisible = _vm.LatestBetaBadgeVisible;
        this.FindControl<TextBlock>("LatestBetaBadgeText")!.Text = _vm.LatestBetaBadgeText;

        this.FindControl<TextBlock>("BackendText")!.Text = _vm.BackendText;
        this.FindControl<TextBlock>("BackendDetailText")!.Text = _vm.BackendLastUpdateSummary;
        this.FindControl<TextBlock>("CatalogueText")!.Text = _vm.CatalogueText;
        this.FindControl<TextBlock>("InstallationHistoryText")!.Text = _vm.InstallationHistoryText;
        this.FindControl<TextBlock>("LastOperationDetailText")!.Text = _vm.LastOperationDetailText;
        this.FindControl<TextBlock>("StageText")!.Text = _vm.StageText;
        var busyIndicator = this.FindControl<Border>("TitlebarBusyIndicator")!;
        busyIndicator.IsVisible = _vm.Busy;
        this.FindControl<TextBlock>("TitlebarBusyText")!.Text = _vm.StageText;
        var busyLabel = Loc.Format("operationInProgress", _vm.StageText);
        ToolTip.SetTip(busyIndicator, busyLabel);
        AutomationProperties.SetName(busyIndicator, busyLabel);
        InstallerTabs.SetActivity(_vm.Busy, _vm.Busy ? busyLabel : null);
        this.FindControl<TextBlock>("TitanfallStatus")!.Text = _vm.IsInstalled
            ? _vm.InstalledText
            : Loc.Get("readyStatus");

        this.FindControl<TextBlock>("VersionPickerTitle")!.Text = _vm.SelectedVersionTitle;
        this.FindControl<TextBlock>("VersionPickerMeta")!.Text = _vm.SelectedVersionMeta;
        var versionGroups = this.FindControl<ItemsControl>("VersionGroupsList");
        if (versionGroups is not null)
        {
            versionGroups.ItemsSource = _vm.VersionGroups;
        }

        var primary = this.FindControl<Button>("PrimaryActionButton")!;
        primary.Content = _vm.PrimaryActionLabel;
        primary.IsEnabled = _vm.CanPrimaryAction && !_vm.Busy;
        this.FindControl<TextBlock>("PrimaryActionHint")!.Text = _vm.PrimaryActionHint;
        ToolTip.SetTip(primary, primary.IsEnabled ? null : _vm.PrimaryActionHint);
        AutomationProperties.SetHelpText(primary, _vm.PrimaryActionHint);
        var launch = this.FindControl<Button>("HeroLaunchButton")!;
        launch.IsVisible = _vm.CanLaunch;
        launch.IsEnabled = _vm.CanLaunch && !_vm.Busy;
        this.FindControl<ProgressBar>("Progress")!.IsVisible = _vm.Busy;
        this.FindControl<Button>("UninstallButton")!.IsEnabled = _vm.CanUninstall && !_vm.Busy;
        this.FindControl<Button>("BrowseReleasesButton")!.IsEnabled = _vm.Versions.Count > 0 && !_vm.Busy;
        this.FindControl<Button>("UpdateBackendButton")!.IsEnabled = !_vm.Busy;
        this.FindControl<Button>("RefreshLocalStateButton")!.IsEnabled = !_vm.Busy;

        this.FindControl<TextBlock>("ChangelogChannelText")!.Text = _vm.ChangelogChannelText;
        this.FindControl<TextBlock>("ChangelogOverlayChannel")!.Text = _vm.ChangelogChannelText;
        this.FindControl<TextBlock>("ChangelogUpdatedText")!.Text = _vm.ChangelogUpdatedText;
        var source = this.FindControl<TextBlock>("ChangelogSourceText")!;
        source.Text = _vm.ChangelogSourceText;
        source.Foreground = BrushFor(_vm.ChangelogSourceText == Loc.Get("cachedData") ? "Amber" : "Green");
        var preview = this.FindControl<TextBlock>("ChangelogPreview")!;
        preview.Text = _changelogExpanded ? _vm.ChangelogFullText : _vm.ChangelogPreview;
        preview.MaxHeight = _changelogExpanded ? double.PositiveInfinity : 82;
        var expand = this.FindControl<Button>("ChangelogExpandButton")!;
        expand.IsVisible = _vm.ChangelogCanExpand;
        expand.Content = Loc.Get(_changelogExpanded ? "showLess" : "showAll");
        this.FindControl<TextBlock>("ChangelogBody")!.Text = _vm.ChangelogFullText;
        this.FindControl<TextBlock>("ChangelogOverlayMeta")!.Text =
            _vm.ChangelogUpdatedText + " · " + _vm.ChangelogSourceText;
        this.FindControl<Button>("OpenChangelogButton")!.Content = Loc.Get("openChangelog");

        SetStatusText("GameDetectedStatus", _vm.GameDetected, "gameDetected", "gameNotDetected");
        SetStatusText("BackendVerifiedStatus", _vm.BackendVerified, "backendVerified", "backendMissing");
        SetStatusText("OpenXrDetectedStatus", _vm.OpenXrDetected, "openXrRuntimeDetected", "openXrRuntimeMissing");
        SetStatusText("DesktopInstalledStatus", _vm.DesktopEntryInstalled, "desktopEntryDetected", "desktopEntryMissingShort");
        SetStatusText("SteamShortcutStatusCompact", _vm.SteamShortcutInstalled, "steamShortcutInstalledShort", "steamShortcutMissingShort");

        ApplyDensityClass();
        ApplyRememberedDetailsState();
        ApplyLogFilter();
        EnsureLogScrollViewer();
    }

    private void ApplyDensityClass()
    {
        var compact = _vm.IsCompactDensity;
        if (compact && !Classes.Contains("density-compact"))
        {
            Classes.Add("density-compact");
        }
        else if (!compact)
        {
            Classes.Remove("density-compact");
        }
    }

    private void ApplyRememberedDetailsState()
    {
        var expander = this.FindControl<Expander>("DetailsExpander");
        if (expander is null || expander.IsExpanded == _vm.DetailsExpanded)
        {
            return;
        }

        _updatingDetailsExpander = true;
        try
        {
            expander.IsExpanded = _vm.DetailsExpanded;
        }
        finally
        {
            _updatingDetailsExpander = false;
        }
    }

    private async void CopyField_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fieldName } button)
        {
            return;
        }
        var text = this.FindControl<TextBlock>(fieldName)?.Text;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (string.IsNullOrEmpty(text) || clipboard is null)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(data);
        button.Classes.Add("copied");
        ToolTip.SetTip(button, Loc.Get("copied"));
        AutomationProperties.SetName(button, Loc.Get("copied"));
        await Task.Delay(1200);
        button.Classes.Remove("copied");
        ToolTip.SetTip(button, Loc.Get("copyVersion"));
        AutomationProperties.SetName(button, Loc.Get("copyVersion"));
    }

    private void LogSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _logFilter = this.FindControl<TextBox>("LogSearchBox")?.Text ?? string.Empty;
        _followLogs = true;
        ApplyLogFilter(follow: true);
    }

    private void ApplyLogFilter(bool follow = false)
    {
        var logsList = this.FindControl<ListBox>("LogsList");
        var empty = this.FindControl<TextBlock>("LogsEmptyText");
        if (logsList is null)
        {
            return;
        }

        var filter = _logFilter.Trim();
        var filtered = filter.Length == 0
            ? _vm.LogLines.ToList()
            : _vm.LogLines.Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!_visibleLogs.SequenceEqual(filtered))
        {
            _visibleLogs = filtered;
            logsList.ItemsSource = _visibleLogs;
            if (follow && _followLogs)
            {
                ScheduleLogScrollToLatest();
            }
        }

        if (empty is not null)
        {
            empty.Text = _vm.LogLines.Count == 0 ? Loc.Get("noLogs") : Loc.Get("noLogMatches");
            empty.IsVisible = _visibleLogs.Count == 0;
        }
        UpdateLogLatestButton();
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ApplyLogFilter(follow: true);
    }

    private void LogsList_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Mouse-wheel-up is an explicit request to inspect older output. Stop
        // following before the smoothed offset starts moving so an incoming log
        // line cannot pull the viewport away from the reader.
        if (e.Delta.Y > 0 && _logScrollViewer is not null
            && _logScrollViewer.Extent.Height > _logScrollViewer.Viewport.Height + 1)
        {
            _followLogs = false;
            UpdateLogLatestButton();
        }
    }

    private void EnsureLogScrollViewer()
    {
        if (_logScrollViewer is not null)
        {
            return;
        }

        var viewer = this.FindControl<ListBox>("LogsList")?
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        _logScrollViewer = viewer;
        // The attached property on the ListBox is consumed by its own template,
        // but our resolver sees the inner ScrollViewer. Set it there as well so
        // the log viewport never chains a boundary wheel into the page.
        ScrollViewer.SetIsScrollChainingEnabled(viewer, false);
        _lastLogOffsetY = viewer.Offset.Y;
        viewer.ScrollChanged += LogsScrollViewer_ScrollChanged;
        if (_followLogs)
        {
            ScheduleLogScrollToLatest();
        }
        UpdateLogLatestButton();
    }

    private void DetachLogScrollViewer()
    {
        if (_logScrollViewer is null)
        {
            return;
        }

        _logScrollViewer.ScrollChanged -= LogsScrollViewer_ScrollChanged;
        _logScrollViewer = null;
    }

    private void LogsScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_logScrollViewer is null)
        {
            return;
        }

        var max = Math.Max(0, _logScrollViewer.Extent.Height - _logScrollViewer.Viewport.Height);
        var current = _logScrollViewer.Offset.Y;
        if (current < _lastLogOffsetY - 0.5 && max - current > 2)
        {
            // The reader moved away from the newest output. Never pull them
            // back while they are inspecting an earlier line.
            _followLogs = false;
        }
        else if (max - current <= 2)
        {
            _followLogs = true;
        }

        _lastLogOffsetY = current;
        UpdateLogLatestButton();
    }

    private void ScheduleLogScrollToLatest()
    {
        if (!_followLogs)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (!_followLogs)
                {
                    return;
                }

                // Set the log viewport directly. Calling ListBox.ScrollIntoView
                // here can bubble a BringIntoView request to the page scroll
                // viewer and move the whole installer while logs are updating.
                if (_logScrollViewer is not null)
                {
                    var max = Math.Max(0, _logScrollViewer.Extent.Height - _logScrollViewer.Viewport.Height);
                    _logScrollViewer.SetCurrentValue(
                        ScrollViewer.OffsetProperty,
                        new Vector(_logScrollViewer.Offset.X, max));
                    _lastLogOffsetY = _logScrollViewer.Offset.Y;
                }

                UpdateLogLatestButton();
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void LogsLatest_Click(object? sender, RoutedEventArgs e)
    {
        _followLogs = true;
        ScheduleLogScrollToLatest();
    }

    private void UpdateLogLatestButton()
    {
        var button = this.FindControl<Button>("LogsLatestButton");
        if (button is null)
        {
            return;
        }

        var canScroll = _logScrollViewer is not null
            && _logScrollViewer.Extent.Height > _logScrollViewer.Viewport.Height + 1;
        button.IsVisible = canScroll && !_followLogs;
    }

    private void SetStatusText(string name, bool ok, string okKey, string missingKey)
    {
        var block = this.FindControl<TextBlock>(name);
        if (block is null)
        {
            return;
        }
        block.Text = "● " + Loc.Get(ok ? okKey : missingKey);
        block.Foreground = BrushFor(ok ? "Green" : "TextDim");
    }

    private static IBrush BrushFor(string key)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.White;
    }

    private static void ApplyCornerStyle(bool rounded)
    {
        if (Application.Current is null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        resources["CornerWindow"] = rounded ? new CornerRadius(12) : default;
        resources["CornerCard"] = rounded ? new CornerRadius(10) : default;
        resources["CornerCardLeft"] = rounded ? new CornerRadius(9, 0, 0, 9) : default;
        resources["CornerInner"] = rounded ? new CornerRadius(6) : default;
        resources["CornerPill"] = rounded ? new CornerRadius(16) : default;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ResizeNorth_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.North, e);
    private void ResizeSouth_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.South, e);
    private void ResizeWest_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.West, e);
    private void ResizeEast_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.East, e);
    private void ResizeNorthWest_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.NorthWest, e);
    private void ResizeNorthEast_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.NorthEast, e);
    private void ResizeSouthWest_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.SouthWest, e);
    private void ResizeSouthEast_Click(object? sender, PointerPressedEventArgs e) => TryResize(WindowEdge.SouthEast, e);

    private void TryResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void SelectTitanfall_Click(object? sender, RoutedEventArgs e)
        => _ = OpenGamePageAsync();

    private void Back_Click(object? sender, RoutedEventArgs e)
        => _ = CloseGamePageAsync();

    private void TitanfallTabs_SelectionChanged(object? sender, TabSelectionChangedEventArgs<TitanfallTab> e)
    {
        if (_switchingTitanfallTab)
        {
            return;
        }

        SaveTitanfallTabScroll(e.Previous);
        ApplyTitanfallTab(e.Selected, animate: true);
    }

    private void SelectTitanfallTab(TitanfallTab tab, bool focusTab, bool animate = true)
    {
        if (InstallerTabs.SelectedTab != tab)
        {
            SaveTitanfallTabScroll(InstallerTabs.SelectedTab);
            _switchingTitanfallTab = true;
            try
            {
                InstallerTabs.SelectTab(tab, focus: false, raiseEvent: false);
            }
            finally
            {
                _switchingTitanfallTab = false;
            }
        }

        ApplyTitanfallTab(tab, animate);
        if (focusTab)
        {
            InstallerTabs.FocusSelectedButton();
        }
    }

    private void SaveTitanfallTabScroll(TitanfallTab tab)
    {
        switch (tab)
        {
            case TitanfallTab.Overview:
                _installerScrollOffset = this.FindControl<ScrollViewer>("InstallerScroll")?.Offset.Y
                    ?? _installerScrollOffset;
                break;
            case TitanfallTab.Releases:
                ReleaseExplorerPanel.SaveScrollState();
                break;
            case TitanfallTab.History:
                HistoryPanel.SaveScrollState();
                break;
            case TitanfallTab.Diagnostics:
                DiagnosticsPanel.SaveScrollState();
                break;
        }
    }

    private void RestoreTitanfallTabScroll(TitanfallTab tab)
    {
        switch (tab)
        {
            case TitanfallTab.Overview:
                var overview = this.FindControl<ScrollViewer>("InstallerScroll");
                var offset = _installerScrollOffset;
                if (overview is null)
                {
                    return;
                }
                void Restore()
                {
                    if (overview.IsVisible)
                    {
                        RestoreScrollOffset(overview, offset);
                    }
                }

                Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
                Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
                break;
            case TitanfallTab.Releases:
                ReleaseExplorerPanel.RestoreScrollState();
                break;
            case TitanfallTab.History:
                HistoryPanel.RestoreScrollState();
                break;
            case TitanfallTab.Diagnostics:
                DiagnosticsPanel.RestoreScrollState();
                break;
        }
    }

    private void ApplyTitanfallTab(TitanfallTab tab, bool animate)
    {
        var overview = this.FindControl<ScrollViewer>("InstallerScroll");
        if (overview is null)
        {
            return;
        }

        // Keep each destination alive. Hiding a view does not touch its
        // ViewModel, filters, expanded rows or scroll offsets.
        overview.IsVisible = tab == TitanfallTab.Overview;
        ReleaseExplorerPanel.IsVisible = tab == TitanfallTab.Releases;
        HistoryPanel.IsVisible = tab == TitanfallTab.History;
        DiagnosticsPanel.IsVisible = tab == TitanfallTab.Diagnostics;

        switch (tab)
        {
            case TitanfallTab.Overview:
                RefreshUi();
                break;
            case TitanfallTab.Releases:
                if (!_releaseTabInitialized)
                {
                    _releaseTabInitialized = true;
                    ReleaseExplorerPanel.Open(_vm, embedded: true);
                }
                else
                {
                    ReleaseExplorerPanel.Activate(_vm);
                }
                break;
            case TitanfallTab.History:
                if (!_historyTabInitialized)
                {
                    _historyTabInitialized = true;
                    HistoryPanel.Open(_vm, embedded: true);
                }
                else
                {
                    HistoryPanel.Activate(_vm);
                }
                break;
            case TitanfallTab.Diagnostics:
                if (!_diagnosticsTabInitialized)
                {
                    _diagnosticsTabInitialized = true;
                    DiagnosticsPanel.Open(_vm);
                }
                else
                {
                    DiagnosticsPanel.Activate(_vm);
                }
                break;
        }

        var target = tab switch
        {
            TitanfallTab.Overview => (Control)overview,
            TitanfallTab.Releases => ReleaseExplorerPanel,
            TitanfallTab.History => HistoryPanel,
            _ => DiagnosticsPanel,
        };
        AnimateTitanfallTab(target, animate);
        RestoreTitanfallTabScroll(tab);
    }

    private static void AnimateTitanfallTab(Control target, bool animate)
    {
        target.Transitions = null;
        if (!animate || !AppearanceManager.AnimationsEnabled)
        {
            target.Opacity = 1;
            target.RenderTransform = IdentityTransform;
            return;
        }

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse("translate(0px, 5px)");
        Dispatcher.UIThread.Post(
            () =>
            {
                target.Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(170),
                        Easing = new CubicEaseOut(),
                    },
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(170),
                        Easing = new CubicEaseOut(),
                    },
                };
                target.Opacity = 1;
                target.RenderTransform = IdentityTransform;
            },
            DispatcherPriority.Loaded);
    }

    private async Task OpenGamePageAsync()
    {
        HelpTip.CloseOpen();
        if (_gameTransitionRunning || _gamePageOpen || SettingsPanel.IsVisible
            || SetupWizardPanel.IsVisible || HistoryPanel.IsVisible
            || ReleaseExplorerPanel.IsVisible
            || this.FindControl<Border>("AttributionOverlay")?.IsVisible == true
            || this.FindControl<Border>("ConfirmOverlay")?.IsVisible == true
            || this.FindControl<Border>("ChangelogOverlay")?.IsVisible == true)
        {
            return;
        }

        var gameScroll = this.FindControl<ScrollViewer>("GameScroll");
        var installerScroll = this.FindControl<ScrollViewer>("InstallerScroll");
        var installerPage = this.FindControl<Grid>("InstallerPage");
        var footer = this.FindControl<StackPanel>("GameFooter");
        var thumbnail = this.FindControl<Border>("TitanfallThumbnail");
        var hero = this.FindControl<Border>("HeroArtwork");
        if (gameScroll is null || installerScroll is null || installerPage is null
            || footer is null || thumbnail is null || hero is null)
        {
            return;
        }

        SelectTitanfallTab(TitanfallTab.Overview, focusTab: false, animate: false);
        installerPage.IsVisible = true;
        installerScroll.IsVisible = true;
        CloseVersionPicker();
        _gameScrollOffset = gameScroll.Offset.Y;
        _gamePageOpen = true;
        _gameTransitionRunning = true;
        _gameTransitionOpening = true;
        var cts = new CancellationTokenSource();
        _gameTransitionCts = cts;
        var generation = ++_gameTransitionVersion;
        var plan = GamePageTransitionPlan.For(AppearanceManager.AnimationsEnabled);
        var groups = InstallerContentGroups();
        var artStarted = false;
        try
        {
            // The destination is laid out invisibly while the outgoing page
            // remains interactive only until the transition starts.
            gameScroll.IsHitTestVisible = false;
            footer.IsHitTestVisible = false;
            installerScroll.IsVisible = true;
            installerScroll.Opacity = 0;
            installerScroll.RenderTransform = IdentityTransform;
            installerScroll.IsHitTestVisible = false;
            PrepareContentGroups(groups, plan.AnimateContent);
            RefreshUi();

            if (!plan.AnimatePages)
            {
                await RestoreScrollAsync(installerScroll, _installerScrollOffset);
                if (generation == _gameTransitionVersion && !cts.IsCancellationRequested)
                {
                    FinishOpenState();
                }
                return;
            }

            await RestoreScrollAsync(installerScroll, _installerScrollOffset);
            await YieldToLayoutAsync();
            if (generation != _gameTransitionVersion || cts.IsCancellationRequested)
            {
                return;
            }

            if (TryMeasureArtwork(
                    thumbnail, gameScroll, hero, installerScroll,
                    out var artworkStart, out var artworkTarget))
            {
                PreparePage(installerScroll, incoming: true);
                PreparePage(gameScroll, incoming: false);
                PreparePage(footer, incoming: false);
                thumbnail.Opacity = 0;
                BeginArtwork(artworkStart, artworkTarget, opening: true, plan.ArtworkDuration);
                artStarted = true;
            }
            else
            {
                HideArtworkLayer();
                PreparePage(installerScroll, incoming: true);
                PreparePage(gameScroll, incoming: false);
                PreparePage(footer, incoming: false);
            }

            AnimatePage(gameScroll, incoming: false, plan.PageOutDuration);
            AnimatePage(installerScroll, incoming: true, plan.PageInDuration);
            AnimatePage(footer, incoming: false, plan.PageOutDuration);
            var contentReveal = RevealContentAsync(groups, plan, cts.Token);

            // The page becomes usable as soon as it is fully opaque; content
            // details may still be settling underneath it.
            await Task.Delay(plan.PageInDuration, cts.Token);
            installerScroll.IsHitTestVisible = true;
            FocusAfterLayout(
                this.FindControl<Button>("BackToGamesButton"),
                installerScroll,
                _installerScrollOffset);

            var remainingArtwork = plan.ArtworkDuration - plan.PageInDuration;
            if (remainingArtwork > TimeSpan.Zero)
            {
                await Task.Delay(remainingArtwork, cts.Token);
            }
            if (artStarted)
            {
                await FadeArtworkAsync(plan.OverlayFadeDuration, cts.Token);
            }
            await contentReveal;

            if (generation == _gameTransitionVersion && !cts.IsCancellationRequested)
            {
                FinishOpenState(focusDestination: false);
            }
        }
        catch (OperationCanceledException)
        {
            // A resize, close, or motion change settled the transition instead.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Game page open transition failed: {ex.Message}");
            if (generation == _gameTransitionVersion)
            {
                FinishGameTransitionNow();
            }
        }
        finally
        {
            if (generation == _gameTransitionVersion)
            {
                if (ReferenceEquals(_gameTransitionCts, cts))
                {
                    _gameTransitionCts = null;
                }
                cts.Dispose();
            }
        }
    }

    private async Task CloseGamePageAsync()
    {
        HelpTip.CloseOpen();
        if (_gameTransitionRunning || !_gamePageOpen)
        {
            return;
        }

        var gameScroll = this.FindControl<ScrollViewer>("GameScroll");
        var installerScroll = this.FindControl<ScrollViewer>("InstallerScroll");
        var installerPage = this.FindControl<Grid>("InstallerPage");
        var footer = this.FindControl<StackPanel>("GameFooter");
        var thumbnail = this.FindControl<Border>("TitanfallThumbnail");
        var hero = this.FindControl<Border>("HeroArtwork");
        if (gameScroll is null || installerScroll is null || installerPage is null
            || footer is null || thumbnail is null || hero is null)
        {
            return;
        }

        SelectTitanfallTab(TitanfallTab.Overview, focusTab: false, animate: false);
        installerPage.IsVisible = true;
        installerScroll.IsVisible = true;
        CloseVersionPicker();
        _installerScrollOffset = installerScroll.Offset.Y;
        _gamePageOpen = false;
        _gameTransitionRunning = true;
        _gameTransitionOpening = false;
        var cts = new CancellationTokenSource();
        _gameTransitionCts = cts;
        var generation = ++_gameTransitionVersion;
        var plan = GamePageTransitionPlan.For(AppearanceManager.AnimationsEnabled);
        var groups = InstallerContentGroups();
        var artStarted = false;
        try
        {
            installerScroll.IsHitTestVisible = false;
            gameScroll.IsVisible = true;
            gameScroll.Opacity = 0;
            gameScroll.RenderTransform = IdentityTransform;
            gameScroll.IsHitTestVisible = false;
            footer.IsVisible = true;
            footer.Opacity = 0;
            footer.RenderTransform = IdentityTransform;
            footer.IsHitTestVisible = false;
            ResetContentGroups(groups);

            if (!plan.AnimatePages)
            {
                await RestoreScrollAsync(gameScroll, _gameScrollOffset);
                if (generation == _gameTransitionVersion && !cts.IsCancellationRequested)
                {
                    FinishCloseState();
                }
                return;
            }

            await RestoreScrollAsync(gameScroll, _gameScrollOffset);
            await YieldToLayoutAsync();
            if (generation != _gameTransitionVersion || cts.IsCancellationRequested)
            {
                return;
            }

            if (TryMeasureArtwork(
                    hero, installerScroll, thumbnail, gameScroll,
                    out var artworkStart, out var artworkTarget))
            {
                PreparePage(gameScroll, incoming: true);
                PreparePage(installerScroll, incoming: false);
                PreparePage(footer, incoming: true);
                thumbnail.Opacity = 0;
                BeginArtwork(artworkStart, artworkTarget, opening: false, plan.ArtworkDuration);
                artStarted = true;
            }
            else
            {
                HideArtworkLayer();
                PreparePage(gameScroll, incoming: true);
                PreparePage(installerScroll, incoming: false);
                PreparePage(footer, incoming: true);
            }

            AnimatePage(installerScroll, incoming: false, plan.PageOutDuration);
            AnimatePage(gameScroll, incoming: true, plan.PageInDuration);
            AnimatePage(footer, incoming: true, plan.PageInDuration);

            await Task.Delay(plan.PageInDuration, cts.Token);
            gameScroll.IsHitTestVisible = true;

            var remainingArtwork = plan.ArtworkDuration - plan.PageInDuration;
            if (remainingArtwork > TimeSpan.Zero)
            {
                await Task.Delay(remainingArtwork, cts.Token);
            }
            if (artStarted)
            {
                await FadeArtworkAsync(plan.OverlayFadeDuration, cts.Token);
            }

            if (generation == _gameTransitionVersion && !cts.IsCancellationRequested)
            {
                FinishCloseState();
            }
        }
        catch (OperationCanceledException)
        {
            // A resize, close, or motion change settled the transition instead.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Game page close transition failed: {ex.Message}");
            if (generation == _gameTransitionVersion)
            {
                FinishGameTransitionNow();
            }
        }
        finally
        {
            if (generation == _gameTransitionVersion)
            {
                if (ReferenceEquals(_gameTransitionCts, cts))
                {
                    _gameTransitionCts = null;
                }
                cts.Dispose();
            }
        }
    }

    private void FinishOpenState(bool focusDestination = true)
    {
        HideArtworkLayer();
        var gameScroll = this.FindControl<ScrollViewer>("GameScroll");
        var installerScroll = this.FindControl<ScrollViewer>("InstallerScroll");
        var installerPage = this.FindControl<Grid>("InstallerPage");
        var footer = this.FindControl<StackPanel>("GameFooter");
        if (installerPage is not null)
        {
            installerPage.IsVisible = true;
        }
        if (gameScroll is not null)
        {
            ResetPage(gameScroll);
            gameScroll.IsVisible = false;
            gameScroll.IsHitTestVisible = true;
        }
        var overviewSelected = InstallerTabs.SelectedTab == TitanfallTab.Overview;
        if (installerScroll is not null)
        {
            ResetPage(installerScroll);
            // A tab clicked during the page transition must remain selected;
            // do not re-show the Overview underneath its content.
            installerScroll.IsVisible = overviewSelected;
            installerScroll.IsHitTestVisible = true;
            if (overviewSelected)
            {
                RestoreScrollOffset(installerScroll, _installerScrollOffset);
            }
        }
        if (footer is not null)
        {
            ResetPage(footer);
            footer.IsVisible = false;
            footer.IsHitTestVisible = true;
        }
        ResetContentGroups(InstallerContentGroups());
        _gamePageOpen = true;
        _gameTransitionRunning = false;
        if (focusDestination)
        {
            if (overviewSelected)
            {
                FocusAfterLayout(
                    this.FindControl<Button>("BackToGamesButton"),
                    installerScroll,
                    _installerScrollOffset);
            }
            else
            {
                InstallerTabs.FocusSelectedButton();
            }
        }
    }

    private void FinishCloseState()
    {
        HideArtworkLayer();
        var gameScroll = this.FindControl<ScrollViewer>("GameScroll");
        var installerScroll = this.FindControl<ScrollViewer>("InstallerScroll");
        var installerPage = this.FindControl<Grid>("InstallerPage");
        var footer = this.FindControl<StackPanel>("GameFooter");
        if (installerPage is not null)
        {
            installerPage.IsVisible = false;
        }
        if (installerScroll is not null)
        {
            ResetPage(installerScroll);
            installerScroll.IsVisible = false;
            installerScroll.IsHitTestVisible = true;
        }
        if (gameScroll is not null)
        {
            ResetPage(gameScroll);
            gameScroll.IsVisible = true;
            gameScroll.IsHitTestVisible = true;
            RestoreScrollOffset(gameScroll, _gameScrollOffset);
        }
        if (footer is not null)
        {
            ResetPage(footer);
            footer.IsVisible = true;
            footer.IsHitTestVisible = true;
        }
        ResetContentGroups(InstallerContentGroups());
        _gamePageOpen = false;
        _gameTransitionRunning = false;
        FocusAfterLayout(
            this.FindControl<Button>("TitanfallCard"),
            gameScroll,
            _gameScrollOffset);
    }

    private void FinishGameTransitionNow()
    {
        if (!_gameTransitionRunning)
        {
            return;
        }

        var opening = _gameTransitionOpening;
        CancelGameTransition();
        if (opening)
        {
            FinishOpenState();
        }
        else
        {
            FinishCloseState();
        }
    }

    private void CancelGameTransition()
    {
        _gameTransitionVersion++;
        _gameTransitionCts?.Cancel();
        _gameTransitionCts?.Dispose();
        _gameTransitionCts = null;
    }

    private IReadOnlyList<Control> InstallerContentGroups()
    {
        var names = new[]
        {
            "InstallerNav",
            "HeroContent",
            "InstalledVersionCard",
            "LatestStableCard",
            "LatestBetaCard",
            "ReleaseSection",
            "BackendSection",
            "ReleaseNotesSection",
            "OverviewDiagnosticsSection",
        };
        var groups = new List<Control>(names.Length);
        foreach (var name in names)
        {
            var group = this.FindControl<Control>(name);
            if (group is not null)
            {
                groups.Add(group);
            }
        }
        return groups;
    }

    private static void PrepareContentGroups(IReadOnlyList<Control> groups, bool animate)
    {
        foreach (var group in groups)
        {
            group.Transitions = null;
            group.Opacity = animate ? 0 : 1;
            group.RenderTransform = animate
                ? TransformOperations.Parse("translate(0px, 8px)")
                : IdentityTransform;
        }
    }

    private static void ResetContentGroups(IReadOnlyList<Control> groups)
    {
        PrepareContentGroups(groups, animate: false);
    }

    private static async Task RevealContentAsync(
        IReadOnlyList<Control> groups,
        GamePageTransitionPlan plan,
        CancellationToken token)
    {
        if (!plan.AnimateContent || groups.Count == 0)
        {
            return;
        }

        for (var index = 0; index < groups.Count; index++)
        {
            var delay = index == 0 ? plan.ContentStartDelay : plan.ContentStagger;
            await Task.Delay(delay, token);
            var group = groups[index];
            group.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = plan.ContentRevealDuration,
                    Easing = new CubicEaseOut(),
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = plan.ContentRevealDuration,
                    Easing = new CubicEaseOut(),
                },
            };
            group.Opacity = 1;
            group.RenderTransform = IdentityTransform;
        }
        await Task.Delay(plan.ContentRevealDuration, token);
    }

    private static void PreparePage(Control page, bool incoming)
    {
        page.Transitions = null;
        page.Opacity = incoming ? 0 : 1;
        page.RenderTransform = incoming
            ? TransformOperations.Parse("translate(0px, 8px)")
            : IdentityTransform;
    }

    private static void AnimatePage(Control page, bool incoming, TimeSpan duration)
    {
        page.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = duration,
                Easing = new CubicEaseOut(),
            },
        };
        page.Opacity = incoming ? 1 : 0;
        page.RenderTransform = incoming
            ? IdentityTransform
            : TransformOperations.Parse("translate(0px, -6px)");
    }

    private static void ResetPage(Control page)
    {
        page.Transitions = null;
        page.Opacity = 1;
        page.RenderTransform = IdentityTransform;
    }

    private bool TryMeasureArtwork(
        Control source,
        ScrollViewer sourceViewport,
        Control target,
        ScrollViewer targetViewport,
        out Rect sourceBounds,
        out Rect targetBounds)
    {
        sourceBounds = default;
        targetBounds = default;
        var layer = this.FindControl<Canvas>("GameTransitionLayer");
        var image = this.FindControl<Image>("GameTransitionArtwork");
        if (layer is null || image?.Source is null
            || !IsUsable(source) || !IsUsable(target)
            || !IsInViewport(source, sourceViewport)
            || !IsInViewport(target, targetViewport))
        {
            return false;
        }

        var measuredSource = BoundsIn(source, layer);
        var measuredTarget = BoundsIn(target, layer);
        if (measuredSource is not { } from || measuredTarget is not { } to
            || from.Width < 2 || from.Height < 2 || to.Width < 2 || to.Height < 2)
        {
            return false;
        }

        sourceBounds = from;
        targetBounds = to;
        return true;
    }

    private void BeginArtwork(Rect start, Rect target, bool opening, TimeSpan duration)
    {
        var layer = this.FindControl<Canvas>("GameTransitionLayer");
        var clip = this.FindControl<Border>("GameTransitionArtworkClip");
        if (layer is null || clip is null)
        {
            return;
        }

        layer.Transitions = null;
        layer.IsVisible = true;
        // On the way back the raw artwork has to take over from the hero's
        // gradient/title stack, so fade it in over the outgoing hero instead
        // of replacing that stack in a single frame.
        layer.Opacity = opening ? 1 : 0;
        clip.Transitions = null;
        clip.Opacity = 1;
        clip.CornerRadius = opening
            ? CornerResource("CornerCardLeft")
            : CornerResource("CornerCard");
        SetArtworkBounds(clip, start);

        var easing = new CubicEaseOut();
        clip.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Canvas.LeftProperty,
                Duration = duration,
                Easing = easing,
            },
            new DoubleTransition
            {
                Property = Canvas.TopProperty,
                Duration = duration,
                Easing = easing,
            },
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty,
                Duration = duration,
                Easing = easing,
            },
            new DoubleTransition
            {
                Property = Layoutable.HeightProperty,
                Duration = duration,
                Easing = easing,
            },
            new CornerRadiusTransition
            {
                Property = Border.CornerRadiusProperty,
                Duration = duration,
                Easing = easing,
            },
        };
        SetArtworkBounds(clip, target);
        clip.CornerRadius = opening
            ? CornerResource("CornerCard")
            : CornerResource("CornerCardLeft");

        if (!opening)
        {
            layer.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(110),
                    Easing = new CubicEaseOut(),
                },
            };
            layer.Opacity = 1;
        }
    }

    private async Task FadeArtworkAsync(TimeSpan duration, CancellationToken token)
    {
        var layer = this.FindControl<Canvas>("GameTransitionLayer");
        if (layer is null || !layer.IsVisible)
        {
            return;
        }

        // Reveal the real artwork beneath the overlay before fading the
        // overlay, so the handoff cannot show a blank frame.
        var thumbnail = this.FindControl<Border>("TitanfallThumbnail");
        if (thumbnail is not null)
        {
            thumbnail.Opacity = 1;
        }

        layer.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new CubicEaseOut(),
            },
        };
        layer.Opacity = 0;
        await Task.Delay(duration, token);
        HideArtworkLayer();
    }

    private void HideArtworkLayer()
    {
        var layer = this.FindControl<Canvas>("GameTransitionLayer");
        var clip = this.FindControl<Border>("GameTransitionArtworkClip");
        var thumbnail = this.FindControl<Border>("TitanfallThumbnail");
        if (layer is not null)
        {
            layer.Transitions = null;
            layer.IsVisible = false;
            layer.Opacity = 1;
        }
        if (clip is not null)
        {
            clip.Transitions = null;
            clip.Width = 0;
            clip.Height = 0;
            Canvas.SetLeft(clip, 0);
            Canvas.SetTop(clip, 0);
        }
        if (thumbnail is not null)
        {
            thumbnail.Opacity = 1;
        }
    }

    private static bool IsUsable(Control control)
        => control.Bounds.Width > 1 && control.Bounds.Height > 1;

    private static bool IsInViewport(Control control, ScrollViewer viewport)
    {
        var origin = control.TranslatePoint(new Point(0, 0), viewport);
        if (origin is null)
        {
            return false;
        }

        var bounds = new Rect(origin.Value, control.Bounds.Size);
        return bounds.Right > 0 && bounds.Bottom > 0
            && bounds.X < viewport.Bounds.Width
            && bounds.Y < viewport.Bounds.Height;
    }

    private static Rect? BoundsIn(Visual element, Visual ancestor)
    {
        var origin = element.TranslatePoint(new Point(0, 0), ancestor);
        return origin is null ? null : new Rect(origin.Value, element.Bounds.Size);
    }

    private static void SetArtworkBounds(Border clip, Rect bounds)
    {
        Canvas.SetLeft(clip, bounds.X);
        Canvas.SetTop(clip, bounds.Y);
        clip.Width = bounds.Width;
        clip.Height = bounds.Height;
    }

    private static CornerRadius CornerResource(string key)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            && value is CornerRadius radius)
        {
            return radius;
        }
        return default;
    }

    private static async Task RestoreScrollAsync(ScrollViewer scroll, double offset)
    {
        await YieldToLayoutAsync();
        RestoreScrollOffset(scroll, offset);
        await YieldToLayoutAsync();
    }

    private static void RestoreScrollOffset(ScrollViewer scroll, double offset)
    {
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.SetCurrentValue(
            ScrollViewer.OffsetProperty,
            new Vector(scroll.Offset.X, Math.Clamp(offset, 0, max)));
    }

    private static async Task YieldToLayoutAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
    }

    private void FocusAfterLayout(Control? target, ScrollViewer? scroll, double offset)
    {
        if (target is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!target.IsVisible)
                {
                    return;
                }
                target.Focus();
                if (scroll is not null)
                {
                    RestoreScrollOffset(scroll, offset);
                }
            },
            DispatcherPriority.Loaded);
    }

    private static readonly TransformOperations IdentityTransform =
        TransformOperations.Parse("translate(0px, 0px)");

    private void OpenSupport_Click(object? sender, RoutedEventArgs e) => OpenUrl(SupportUrl);

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { path },
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not open {path}: {ex.Message}");
        }
    }

    private void OpenUrl(string url)
    {
        // Desktop-standard URL opening; never spawns a terminal and does not claim success.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { url },
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not open {url}: {ex.Message}");
        }
    }

    private void OpenHistory_Click(object? sender, RoutedEventArgs e)
    {
        HelpTip.CloseOpen();
        if (_gameTransitionRunning || SetupWizardPanel.IsVisible || SettingsPanel.IsVisible)
        {
            return;
        }
        CloseVersionPicker();
        SelectTitanfallTab(TitanfallTab.History, focusTab: true);
    }

    private void BrowseReleases_Click(object? sender, RoutedEventArgs e)
    {
        HelpTip.CloseOpen();
        if (_gameTransitionRunning || SetupWizardPanel.IsVisible || SettingsPanel.IsVisible)
        {
            return;
        }
        CloseVersionPicker();
        SelectTitanfallTab(TitanfallTab.Releases, focusTab: true);
    }

    private void OpenDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        HelpTip.CloseOpen();
        if (_gameTransitionRunning || SetupWizardPanel.IsVisible || SettingsPanel.IsVisible)
        {
            return;
        }
        CloseVersionPicker();
        SelectTitanfallTab(TitanfallTab.Diagnostics, focusTab: true);
    }

    private void VerifyDiagnostics_Click(object? sender, RoutedEventArgs e)
        => OpenDiagnostics_Click(sender, e);

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        HelpTip.CloseOpen();
        if (_gameTransitionRunning || SetupWizardPanel.IsVisible)
        {
            return;
        }

        CloseVersionPicker();
        _vm.RefreshLocalState();
        this.FindControl<SettingsView>("SettingsPanel")!.Open(_vm);
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanInstall)
        {
            return;
        }

        var confirmed = await ConfirmAsync(
            Loc.Get("confirmInstallTitle"),
            Loc.Format(
                "confirmInstallBody",
                _vm.SelectedVersionTitle,
                _vm.SelectedVersionNumberText,
                _vm.InstalledVersionText,
                _vm.InstallTargetText),
            Loc.Get("install"));
        if (!confirmed)
        {
            return;
        }

        await _vm.InstallSelectedAsync();
        RefreshUi();
    }

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.RunPreflightAsync();
        RefreshUi();
    }

    private async void UpdateBackend_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.Busy)
        {
            return;
        }
        await _vm.UpdateBackendAsync();
        RefreshUi();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.Busy)
        {
            return;
        }
        await Task.Run(_vm.RefreshLocalState);
        await _vm.RefreshCatalogueAsync();
        RefreshUi();
    }

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmAsync(
            Loc.Get("uninstallTitle"),
            Loc.Get("uninstallText"),
            Loc.Get("uninstall"));
        if (!confirmed)
        {
            return;
        }
        await _vm.UninstallAsync();
        RefreshUi();
    }

    private void Launch_Click(object? sender, RoutedEventArgs e)
    {
        _vm.LaunchVr();
        RefreshUi();
    }

    private async void RepairDesktop_Click(object? sender, RoutedEventArgs e)
    {
        await _vm.RepairDesktopAsync();
        RefreshUi();
    }

    private async Task RepairSteamFromDiagnosticsAsync()
    {
        if (!_vm.CanModifySteamNow(out var reason))
        {
            await ShowInfoAsync(Loc.Get("steamBlockedTitle"), reason ?? Loc.Get("steamBlockedText"));
            return;
        }
        if (_vm.IsSteamRunning && _vm.ConfirmSteamRestart)
        {
            var confirmed = await ConfirmAsync(
                Loc.Get("repairSteamTitle"),
                Loc.Get("repairSteamText"),
                Loc.Get("restartSteamRepair"));
            if (!confirmed)
            {
                return;
            }
        }

        await _vm.AddOrUpdateSteamAsync(allowRestart: true);
        RefreshUi();
        DiagnosticsPanel.RefreshAfterOperation();
    }

    private async void AddSteam_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanModifySteamNow(out var reason))
        {
            await ShowInfoAsync(Loc.Get("steamBlockedTitle"), reason ?? Loc.Get("steamBlockedText"));
            return;
        }
        if (_vm.IsSteamRunning && _vm.ConfirmSteamRestart)
        {
            var confirmed = await ConfirmAsync(
                Loc.Get("addSteamTitle"),
                Loc.Get("addSteamText"),
                Loc.Get("restartSteamAdd"));
            if (!confirmed)
            {
                return;
            }
        }

        await _vm.AddOrUpdateSteamAsync(allowRestart: true);
        RefreshUi();
    }

    private async void RemoveSteam_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanModifySteamNow(out var reason))
        {
            await ShowInfoAsync(Loc.Get("steamBlockedTitle"), reason ?? Loc.Get("steamBlockedText"));
            return;
        }
        if (_vm.IsSteamRunning && _vm.ConfirmSteamRestart)
        {
            var confirmed = await ConfirmAsync(
                Loc.Get("removeSteamTitle"),
                Loc.Get("removeSteamText"),
                Loc.Get("restartSteamRemove"));
            if (!confirmed)
            {
                return;
            }
        }
        await _vm.RemoveSteamShortcutAsync(allowRestart: true);
        RefreshUi();
    }

    private void VersionPicker_Click(object? sender, RoutedEventArgs e)
    {
        HelpTip.CloseOpen();
        var popup = this.FindControl<Popup>("VersionPopup")!;
        if (sender is Button target)
        {
            // The control is inside a scaled root; re-assert the anchor before
            // opening so the flyout is positioned from the actual selector.
            popup.PlacementTarget = target;
        }
        popup.IsOpen = !popup.IsOpen;
        if (popup.IsOpen)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                FocusSelectedVersion,
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void FocusSelectedVersion()
    {
        var popup = this.FindControl<Popup>("VersionPopup");
        if (popup?.IsOpen != true)
        {
            return;
        }

        var buttons = VersionButtons();
        var selected = buttons.FirstOrDefault(button => button.Classes.Contains("selected"));
        (selected ?? buttons.FirstOrDefault())?.Focus();
    }

    private void VersionPopup_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseVersionPicker();
            e.Handled = true;
            return;
        }

        var direction = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.PageUp => -5,
            Key.PageDown => 5,
            _ => 0,
        };
        var buttons = VersionButtons();
        if (buttons.Count == 0)
        {
            return;
        }

        int index;
        if (e.Key == Key.Home)
        {
            index = 0;
        }
        else if (e.Key == Key.End)
        {
            index = buttons.Count - 1;
        }
        else if (direction != 0)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Button;
            var current = focused is null ? -1 : buttons.IndexOf(focused);
            if (current < 0)
            {
                current = buttons.FindIndex(button => button.Classes.Contains("selected"));
            }
            index = Math.Clamp(current + direction, 0, buttons.Count - 1);
        }
        else
        {
            return;
        }

        buttons[index].Focus();
        e.Handled = true;
    }

    private System.Collections.Generic.List<Button> VersionButtons()
    {
        var panel = this.FindControl<Border>("VersionPopupPanel");
        return panel is null
            ? new System.Collections.Generic.List<Button>()
            : panel.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("versionrow"))
                .ToList();
    }

    private void CloseVersionPicker_Click(object? sender, RoutedEventArgs e) => CloseVersionPicker();

    private void VersionOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VersionOption option })
        {
            _vm.SelectVersion(option);
            CloseVersionPicker();
            RefreshUi();
        }
    }

    private void CloseVersionPicker()
    {
        var popup = this.FindControl<Popup>("VersionPopup");
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    private void ChangelogExpand_Click(object? sender, RoutedEventArgs e)
    {
        var offset = this.FindControl<ScrollViewer>("InstallerScroll")?.Offset.Y ?? 0;
        _changelogExpanded = !_changelogExpanded;
        RefreshUi();
        RestoreMainOffset(offset);
    }

    private void DetailsExpander_Toggled(object? sender, RoutedEventArgs e)
    {
        var offset = this.FindControl<ScrollViewer>("InstallerScroll")?.Offset.Y ?? 0;
        RestoreMainOffset(offset);
        if (!_updatingDetailsExpander && sender is Expander expander)
        {
            _vm.SetDetailsExpanded(expander.IsExpanded);
        }
    }

    private void RestoreMainOffset(double offset)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                var scroll = this.FindControl<ScrollViewer>("InstallerScroll");
                if (scroll is null)
                {
                    return;
                }
                RestoreScrollOffset(scroll, offset);
            },
            DispatcherPriority.Loaded);
    }

    private void Changelog_Click(object? sender, RoutedEventArgs e)
    {
        _changelogExpanded = true;
        this.FindControl<Border>("ChangelogOverlay")!.IsVisible = true;
        RefreshUi();
    }

    private void CloseChangelog_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Border>("ChangelogOverlay")!.IsVisible = false;
    }

    private async Task<bool> ConfirmAsync(string title, string text, string okLabel)
    {
        this.FindControl<TextBlock>("ConfirmTitle")!.Text = title;
        this.FindControl<TextBlock>("ConfirmText")!.Text = text;
        this.FindControl<Button>("ConfirmOkButton")!.Content = okLabel;
        this.FindControl<Button>("ConfirmCancelButton")!.IsVisible = true;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = true;
        ResetConfirmScroll();

        _confirmation = new TaskCompletionSource<bool>();
        var result = await _confirmation.Task;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = false;
        return result;
    }

    private async Task ShowInfoAsync(string title, string text)
    {
        this.FindControl<TextBlock>("ConfirmTitle")!.Text = title;
        this.FindControl<TextBlock>("ConfirmText")!.Text = text;
        this.FindControl<Button>("ConfirmOkButton")!.Content = Loc.Get("close");
        this.FindControl<Button>("ConfirmCancelButton")!.IsVisible = false;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = true;
        ResetConfirmScroll();

        _confirmation = new TaskCompletionSource<bool>();
        await _confirmation.Task;
        this.FindControl<Border>("ConfirmOverlay")!.IsVisible = false;
    }

    private void ResetConfirmScroll()
    {
        var scroll = this.FindControl<ScrollViewer>("ConfirmScroll");
        if (scroll is null)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => scroll.SetCurrentValue(ScrollViewer.OffsetProperty, default),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void ConfirmOk_Click(object? sender, RoutedEventArgs e) => _confirmation?.TrySetResult(true);

    private void ConfirmCancel_Click(object? sender, RoutedEventArgs e) => _confirmation?.TrySetResult(false);
}
