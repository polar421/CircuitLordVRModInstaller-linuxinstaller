using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitLord.VRModInstaller.Appearance;
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
    private double _lastLogOffsetY;

    public MainWindow()
    {
        InitializeComponent();
        // Return keyboard focus to the title-bar action after the closing
        // animation has removed the modal from the visual tree.
        SettingsPanel.Closed += (_, _) =>
            this.FindControl<Button>("SettingsButton")?.Focus();
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
        SizeChanged += (_, _) => ApplyScale();
        Closed += (_, _) =>
        {
            AppearanceManager.ScaleChanged -= Appearance_Changed;
            AppearanceManager.MotionChanged -= Appearance_Changed;
            AppearanceManager.Theme.Changed -= Theme_Changed;
            _vm.LogLines.CollectionChanged -= LogLines_CollectionChanged;
            DetachLogScrollViewer();
        };
        ApplyScale();
        ApplyMotionClasses();
        DataContext = _vm;
        _vm.Changed += (_, _) => RefreshUi();
        _vm.LogLines.CollectionChanged += LogLines_CollectionChanged;
        var logsList = this.FindControl<ListBox>("LogsList")!;
        logsList.Loaded += (_, _) => EnsureLogScrollViewer();
        logsList.AddHandler(
            InputElement.PointerWheelChangedEvent,
            LogsList_PointerWheelChanged,
            RoutingStrategies.Tunnel);
        // Auto-update starts a detached helper and then asks the window to close.
        // The helper waits for this process before swapping the app directory.
        _vm.RestartRequested += (_, _) => Close();
        Opened += async (_, _) =>
        {
            // Show the notice immediately while backend initialization proceeds.
            ShowAttributionNotice();
            await _vm.InitializeAsync();
            RefreshUi();
        };
    }

    private void Appearance_Changed(object? sender, EventArgs e)
    {
        ApplyScale();
        ApplyMotionClasses();
    }

    private void Theme_Changed(object? sender, EventArgs e)
    {
        // Brushes assigned from code-behind are not DynamicResource bindings;
        // re-apply them after the variant changes.
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshUi);
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
        this.FindControl<Button>("TitanfallCard")?.Focus();
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
        var popup = this.FindControl<Popup>("VersionPopup");
        if (popup?.IsOpen == true)
        {
            popup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (SettingsPanel.HandleEscape())
        {
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
        this.FindControl<TextBlock>("ChannelValue")!.Text = _vm.InstalledChannelText;
        // Not installed is already the primary state; repeating it under
        // "Installed version" and in the hero adds no information.
        this.FindControl<TextBlock>("ChannelValue")!.IsVisible = _vm.IsInstalled;

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
        this.FindControl<TextBlock>("GameText")!.Text = _vm.GameText;
        this.FindControl<TextBlock>("PreflightText")!.Text = _vm.PreflightText;
        this.FindControl<TextBlock>("DesktopText")!.Text = _vm.DesktopText;
        this.FindControl<TextBlock>("SteamText")!.Text = _vm.SteamText;
        this.FindControl<TextBlock>("RuntimeText")!.Text = _vm.RuntimeText;
        this.FindControl<TextBlock>("StageText")!.Text = _vm.StageText;
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
        var launch = this.FindControl<Button>("HeroLaunchButton")!;
        launch.IsVisible = _vm.CanLaunch;
        launch.IsEnabled = _vm.CanLaunch && !_vm.Busy;
        this.FindControl<ProgressBar>("Progress")!.IsVisible = _vm.Busy;
        this.FindControl<Button>("UninstallButton")!.IsEnabled = _vm.CanUninstall && !_vm.Busy;
        this.FindControl<Button>("SteamButton")!.IsEnabled = _vm.HasSteamAccount && !_vm.Busy;
        this.FindControl<Button>("SteamRemoveButton")!.IsEnabled = _vm.HasSteamAccount && !_vm.Busy;

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

        var logsList = this.FindControl<ListBox>("LogsList")!;
        if (!ReferenceEquals(logsList.ItemsSource, _vm.LogLines))
        {
            logsList.ItemsSource = _vm.LogLines;
        }
        EnsureLogScrollViewer();
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followLogs)
        {
            ScheduleLogScrollToLatest();
        }
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
    {
        CloseVersionPicker();
        var scroll = this.FindControl<ScrollViewer>("MainScroll")!;
        _gameScrollOffset = scroll.Offset.Y;
        this.FindControl<StackPanel>("GameSelect")!.IsVisible = false;
        this.FindControl<StackPanel>("GameFooter")!.IsVisible = false;
        this.FindControl<StackPanel>("InstallerView")!.IsVisible = true;
        RefreshUi();
        RestoreMainScroll(_installerScrollOffset);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        CloseVersionPicker();
        var scroll = this.FindControl<ScrollViewer>("MainScroll")!;
        _installerScrollOffset = scroll.Offset.Y;
        this.FindControl<StackPanel>("GameSelect")!.IsVisible = true;
        this.FindControl<StackPanel>("GameFooter")!.IsVisible = true;
        this.FindControl<StackPanel>("InstallerView")!.IsVisible = false;
        RestoreMainScroll(_gameScrollOffset);
    }

    private void RestoreMainScroll(double offset)
    {
        var scroll = this.FindControl<ScrollViewer>("MainScroll");
        if (scroll is null)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.SetCurrentValue(
                    ScrollViewer.OffsetProperty,
                    new Vector(scroll.Offset.X, Math.Clamp(offset, 0, max)));
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OpenSupport_Click(object? sender, RoutedEventArgs e) => OpenUrl(SupportUrl);

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

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        CloseVersionPicker();
        _vm.RefreshLocalState();
        this.FindControl<SettingsView>("SettingsPanel")!.Open(_vm);
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
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
        await _vm.UpdateBackendAsync();
        RefreshUi();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
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
        var offset = this.FindControl<ScrollViewer>("MainScroll")?.Offset.Y ?? 0;
        _changelogExpanded = !_changelogExpanded;
        RefreshUi();
        RestoreMainOffset(offset);
    }

    private void DetailsExpander_Toggled(object? sender, RoutedEventArgs e)
    {
        var offset = this.FindControl<ScrollViewer>("MainScroll")?.Offset.Y ?? 0;
        RestoreMainOffset(offset);
    }

    private void RestoreMainOffset(double offset)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                var scroll = this.FindControl<ScrollViewer>("MainScroll");
                if (scroll is null)
                {
                    return;
                }
                var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.SetCurrentValue(
                    ScrollViewer.OffsetProperty,
                    new Vector(scroll.Offset.X, Math.Clamp(offset, 0, max)));
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
