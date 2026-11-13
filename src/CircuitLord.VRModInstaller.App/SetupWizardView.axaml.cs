using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// First-run setup. The wizard is presentation-only: every action delegates to
/// the existing installer view model and its verified backend/integration
/// services, so it cannot diverge from the installer screen.
/// </summary>
public partial class SetupWizardView : UserControl
{
    private const int StepCount = 9;

    private enum Step
    {
        Welcome = 0,
        Game = 1,
        Backend = 2,
        Dependencies = 3,
        OpenXr = 4,
        WiVrn = 5,
        Steam = 6,
        Release = 7,
        Finish = 8,
    }

    private enum StatusKind
    {
        Ready,
        Attention,
        Error,
        Neutral,
    }

    private InstallerViewModel? _vm;
    private Step _step = Step.Welcome;
    private string _releaseChoice = "latest";
    private bool _actionRunning;
    private bool _rendering;

    public SetupWizardView()
    {
        InitializeComponent();
    }

    public event EventHandler? Closed;

    public bool IsOpen => IsVisible;

    public void Open(InstallerViewModel vm)
    {
        _vm = vm;
        _step = Step.Welcome;
        _actionRunning = false;
        _releaseChoice = vm.SelectedReleaseToken == "beta"
            || (vm.SelectedReleaseToken is null && vm.SelectedVersion?.GroupKey == "beta")
            ? "beta"
            : "latest";
        vm.RefreshLocalState();
        IsVisible = true;
        Render();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible)
                {
                    Focus();
                    WizardPrimaryButton.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    public bool HandleEscape()
    {
        if (!IsVisible)
        {
            return false;
        }
        CloseWizard(markCompleted: false);
        return true;
    }

    /// <summary>Re-applies theme resources after a variant change.</summary>
    public void RefreshAppearance()
    {
        if (IsVisible && !_rendering)
        {
            Render();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseWizard(markCompleted: false);

    private void SkipSetup_Click(object? sender, RoutedEventArgs e) => CloseWizard(markCompleted: true);

    private void CloseWizard(bool markCompleted)
    {
        HelpTip.CloseOpen();
        if (markCompleted)
        {
            _vm?.MarkSetupWizardCompleted();
        }
        IsVisible = false;
        _vm = null;
        _actionRunning = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Primary_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _actionRunning)
        {
            return;
        }

        if (_step == Step.Finish)
        {
            CloseWizard(markCompleted: true);
            return;
        }

        if (_step == Step.Release)
        {
            _vm.SelectReleaseToken(_releaseChoice);
        }

        MoveNext();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _actionRunning || _step == Step.Welcome)
        {
            return;
        }
        SetStep((Step)((int)_step - 1));
    }

    private void SkipStep_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _actionRunning || _step is Step.Welcome or Step.Finish)
        {
            return;
        }
        MoveNext();
    }

    private void MoveNext()
    {
        if (_vm is null)
        {
            return;
        }

        var next = (int)_step + 1;
        while (next < (int)Step.Finish && IsConfigured((Step)next))
        {
            next++;
        }
        SetStep((Step)Math.Min(next, (int)Step.Finish));
    }

    private void SetStep(Step step)
    {
        _step = step;
        Render();
        WizardScroll.Offset = new Vector(0, 0);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible)
                {
                    WizardPrimaryButton.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    private bool IsConfigured(Step step) => _vm is not null && step switch
    {
        Step.Game => _vm.GameDetected,
        Step.Backend => _vm.BackendVerified,
        Step.Dependencies => _vm.PreflightSucceeded,
        Step.OpenXr => _vm.OpenXrDetected,
        Step.WiVrn => _vm.DesktopEntryInstalled,
        Step.Steam => _vm.SteamShortcutInstalled,
        Step.Release => _vm.SelectedReleaseToken is "latest" or "beta",
        _ => false,
    };

    private static bool IsOptional(Step step) => step is Step.WiVrn or Step.Steam;

    private void Render()
    {
        if (_vm is null || _rendering)
        {
            return;
        }

        _rendering = true;
        try
        {
            var index = (int)_step;
            WizardStepText.Text = Loc.Format("wizardStepOf", index + 1, StepCount);
            WizardProgress.Value = index + 1;
            WizardHeroPanel.IsVisible = _step == Step.Welcome;
            WizardWelcomePanel.IsVisible = _step == Step.Welcome;
            WizardStatusCard.IsVisible = _step != Step.Welcome;
            WizardTechnicalPanel.IsVisible = false;
            WizardReleasePanel.IsVisible = _step == Step.Release;
            WizardSummaryPanel.IsVisible = _step == Step.Finish;
            WizardOptionalBadge.IsVisible = IsOptional(_step);

            var centered = _step == Step.Welcome;
            WizardStepTitle.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
            WizardDescription.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
            WizardStepTitle.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            WizardDescription.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            WizardEyebrow.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            SetHeading();
            RenderPhases();
            RenderStatus();
            RenderActions();
            RenderReleaseChoices();

            WizardSkipStepButton.IsVisible = _step is not (Step.Welcome or Step.Finish);
            WizardSkipSetupButton.IsVisible = _step != Step.Finish;
            WizardBackButton.IsVisible = _step != Step.Welcome;
            WizardPrimaryButton.Content = _step switch
            {
                Step.Welcome => Loc.Get("wizardStart"),
                Step.Finish => Loc.Get("wizardFinish"),
                _ => Loc.Get("continueLabel"),
            };

            var enabled = !_actionRunning && !_vm.Busy;
            WizardPrimaryButton.IsEnabled = enabled;
            WizardBackButton.IsEnabled = enabled;
            WizardSkipStepButton.IsEnabled = enabled;
            WizardSkipSetupButton.IsEnabled = !_actionRunning;
            WizardCloseButton.IsEnabled = !_actionRunning;
            GameActionButton.IsEnabled = enabled;
            BackendActionButton.IsEnabled = enabled;
            DependenciesActionButton.IsEnabled = enabled;
            RuntimeActionButton.IsEnabled = enabled;
            WiVrnActionButton.IsEnabled = enabled;
            SteamActionButton.IsEnabled = enabled;

            if (_step == Step.Finish)
            {
                BuildSummary();
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private void SetHeading()
    {
        var eyebrow = _step switch
        {
            Step.Welcome => "wizardPhaseWelcome",
            Step.Finish => "wizardPhaseLaunch",
            _ => "wizardPhaseSetup",
        };
        WizardEyebrow.Text = Loc.Get(eyebrow);
        WizardStepTitle.Text = _step switch
        {
            Step.Welcome => Loc.Get("wizardWelcomeTitle"),
            Step.Game => Loc.Get("wizardGameTitle"),
            Step.Backend => Loc.Get("wizardBackendTitle"),
            Step.Dependencies => Loc.Get("wizardDependenciesTitle"),
            Step.OpenXr => Loc.Get("wizardOpenXrTitle"),
            Step.WiVrn => Loc.Get("wizardWiVrnTitle"),
            Step.Steam => Loc.Get("wizardSteamTitle"),
            Step.Release => Loc.Get("wizardReleaseTitle"),
            _ => Loc.Get("wizardFinishTitle"),
        };
        WizardDescription.Text = _step switch
        {
            Step.Welcome => Loc.Get("wizardWelcomeBody"),
            Step.Game => Loc.Get("wizardGameBody"),
            Step.Backend => Loc.Get("wizardBackendBody"),
            Step.Dependencies => Loc.Get("preflightHint"),
            Step.OpenXr => Loc.Get("runtimeInfoNote"),
            Step.WiVrn => Loc.Get("wizardWiVrnBody"),
            Step.Steam => Loc.Get("steamIntegrationDescription"),
            Step.Release => Loc.Get("wizardReleaseBody"),
            _ => Loc.Get("wizardFinishBody"),
        };
    }

    private void RenderPhases()
    {
        var phase = _step switch
        {
            Step.Welcome => 0,
            Step.Finish => 2,
            _ => 1,
        };
        StylePhase(Phase1Dot, Phase1Number, Phase1Label, Phase1Hint, phase, 0);
        StylePhase(Phase2Dot, Phase2Number, Phase2Label, Phase2Hint, phase, 1);
        StylePhase(Phase3Dot, Phase3Number, Phase3Label, Phase3Hint, phase, 2);
    }

    private void StylePhase(
        Border dot, TextBlock number, TextBlock label, TextBlock hint, int currentPhase, int phase)
    {
        var active = currentPhase >= phase;
        var current = currentPhase == phase;
        dot.Background = BrushFor(current ? "Accent" : active ? "Green" : "Hover");
        number.Foreground = BrushFor(active ? "TextOnFill" : "TextDim");
        label.Foreground = BrushFor(active ? "Text" : "Muted");
        hint.Foreground = BrushFor(active ? "TextDim" : "Muted");
    }

    private void RenderStatus()
    {
        if (_step == Step.Welcome || _vm is null)
        {
            return;
        }

        var status = _step switch
        {
            Step.Game => GameStatus(),
            Step.Backend => BackendStatus(),
            Step.Dependencies => DependenciesStatus(),
            Step.OpenXr => OpenXrStatus(),
            Step.WiVrn => WiVrnStatus(),
            Step.Steam => SteamStatus(),
            Step.Release => ReleaseStatus(),
            _ => ReadinessStatus(),
        };

        WizardStatusCard.Background = BrushFor(status.Kind switch
        {
            StatusKind.Ready => "GreenFill",
            StatusKind.Attention => "AmberFill",
            StatusKind.Error => "RedFill",
            _ => "Bg",
        });
        WizardStatusCard.BorderBrush = BrushFor(
            status.Kind == StatusKind.Ready ? "SuccessBorder" : "Stroke");
        WizardStatusDot.Background = BrushFor(status.Kind switch
        {
            StatusKind.Ready => "Green",
            StatusKind.Attention => "Amber",
            StatusKind.Error => "Red",
            _ => "Muted",
        });
        WizardStatusTitle.Text = status.Title;
        WizardStatusDetail.Text = status.Detail;
        WizardStatusDetail.IsVisible = !string.IsNullOrWhiteSpace(status.Detail);

        var showTechnical = _step == Step.Dependencies && _vm.PreflightHasRun
            && !string.IsNullOrWhiteSpace(_vm.PreflightText);
        WizardTechnicalPanel.IsVisible = showTechnical;
        if (showTechnical)
        {
            WizardTechnicalOutput.Text = _vm.PreflightText;
        }
    }

    private (StatusKind Kind, string Title, string Detail) GameStatus()
    {
        if (_vm!.GameDetected)
        {
            return (StatusKind.Ready, Loc.Get("gameDetected"), _vm.TitanfallGameDirectory);
        }
        return (StatusKind.Attention, Loc.Get("gameNotDetected"), Loc.Get("wizardGameMissingDetail"));
    }

    private (StatusKind Kind, string Title, string Detail) BackendStatus()
    {
        if (_vm!.BackendVerified)
        {
            return (StatusKind.Ready, Loc.Get("backendVerified"), _vm.BackendText);
        }
        return (StatusKind.Attention, Loc.Get("backendMissing"), _vm.BackendText);
    }

    private (StatusKind Kind, string Title, string Detail) DependenciesStatus()
    {
        if (!_vm!.PreflightHasRun)
        {
            return (StatusKind.Neutral, Loc.Get("wizardDependenciesNotRun"), Loc.Get("wizardDependenciesNotRunDetail"));
        }
        return _vm.PreflightSucceeded
            ? (StatusKind.Ready, Loc.Get("wizardDependenciesPassed"), _vm.LastPreflightText)
            : (StatusKind.Error, Loc.Get("wizardDependenciesFailed"), _vm.LastPreflightText);
    }

    private (StatusKind Kind, string Title, string Detail) OpenXrStatus()
    {
        if (_vm!.OpenXrDetected)
        {
            return (StatusKind.Ready, Loc.Get("openXrRuntimeDetected"), _vm.OpenXrStatusText);
        }
        return (StatusKind.Attention, Loc.Get("openXrRuntimeMissing"), Loc.Get("wizardOpenXrMissingDetail"));
    }

    private (StatusKind Kind, string Title, string Detail) WiVrnStatus()
    {
        if (_vm!.DesktopEntryInstalled)
        {
            return (StatusKind.Ready, Loc.Get("desktopEntryDetected"), _vm.DesktopEntryStatusText);
        }
        if (_vm.WiVrnAvailable)
        {
            return (StatusKind.Attention, Loc.Get("desktopEntryMissingShort"), _vm.DesktopEntryStatusText);
        }
        return (StatusKind.Neutral, Loc.Get("notDetected"), Loc.Get("wizardWiVrnNotDetectedDetail"));
    }

    private (StatusKind Kind, string Title, string Detail) SteamStatus()
    {
        if (_vm!.SteamShortcutInstalled)
        {
            return (StatusKind.Ready, Loc.Get("steamShortcutInstalledShort"), _vm.SteamShortcutStatusText);
        }
        if (!_vm.SteamDetected)
        {
            return (StatusKind.Neutral, Loc.Get("steamNotDetected"), Loc.Get("steamIntegrationDescription"));
        }
        if (!_vm.HasLauncher)
        {
            return (StatusKind.Neutral, Loc.Get("launcherMissing"), Loc.Get("wizardSteamNeedsLauncherDetail"));
        }
        return (StatusKind.Attention, Loc.Get("steamShortcutMissingShort"), _vm.SteamText);
    }

    private (StatusKind Kind, string Title, string Detail) ReleaseStatus()
    {
        if (!_vm!.StableReleaseAvailable && !_vm.BetaReleaseAvailable)
        {
            return (StatusKind.Attention, Loc.Get("catalogueUnavailable"), Loc.Get("wizardReleaseUnavailableDetail"));
        }
        var token = _vm.SelectedReleaseToken;
        if (token is "latest" or "beta")
        {
            return (StatusKind.Ready, Loc.Get(token == "beta" ? "betaReleases" : "stableLabel"),
                _vm.SelectedVersionMeta);
        }
        return (StatusKind.Attention, Loc.Get("chooseRelease"), Loc.Get("wizardReleaseChooseDetail"));
    }

    private (StatusKind Kind, string Title, string Detail) ReadinessStatus()
    {
        var ready = _vm!.GameDetected && _vm.BackendVerified && _vm.PreflightSucceeded && _vm.OpenXrDetected;
        return ready
            ? (StatusKind.Ready, Loc.Get("wizardReadyTitle"), Loc.Get("wizardReadyBody"))
            : (StatusKind.Attention, Loc.Get("wizardNotReadyTitle"), Loc.Get("wizardNotReadyBody"));
    }

    private void RenderActions()
    {
        var vm = _vm!;
        var showGame = _step == Step.Game && !vm.GameDetected;
        var showBackend = _step == Step.Backend && !vm.BackendVerified;
        var showDependencies = _step == Step.Dependencies;
        var showRuntime = _step == Step.OpenXr;
        var showWiVrn = _step == Step.WiVrn && vm.WiVrnAvailable
            && !vm.DesktopEntryInstalled && vm.HasLauncher;
        var showSteam = _step == Step.Steam && vm.SteamDetected
            && vm.HasSteamAccount && vm.HasLauncher && !vm.SteamShortcutInstalled;

        GameActionButton.IsVisible = showGame;
        BackendActionButton.IsVisible = showBackend;
        DependenciesActionButton.IsVisible = showDependencies;
        RuntimeActionButton.IsVisible = showRuntime;
        WiVrnActionButton.IsVisible = showWiVrn;
        SteamActionButton.IsVisible = showSteam;
        WizardActionsPanel.IsVisible = showGame || showBackend || showDependencies
            || showRuntime || showWiVrn || showSteam;

        string? hint = null;
        if (_step == Step.WiVrn && !vm.WiVrnAvailable)
        {
            hint = Loc.Get("wizardWiVrnNotDetectedDetail");
        }
        else if (_step == Step.WiVrn && vm.WiVrnAvailable && !vm.DesktopEntryInstalled && !vm.HasLauncher)
        {
            hint = Loc.Get("wizardWiVrnAfterInstall");
        }
        else if (_step == Step.Steam && !vm.SteamDetected)
        {
            hint = Loc.Get("steamIntegrationDescription");
        }
        else if (_step == Step.Steam && !vm.HasLauncher)
        {
            hint = Loc.Get("wizardSteamNeedsLauncherDetail");
        }
        else if (_step == Step.Steam && vm.HasSteamAccount && vm.IsSteamRunning)
        {
            hint = Loc.Get("steamSessionActive");
        }
        else if (_step == Step.Dependencies && vm.PreflightHasRun && !vm.PreflightSucceeded)
        {
            hint = Loc.Get("wizardDependenciesRetryHint");
        }

        WizardActionHint.Text = hint ?? string.Empty;
        WizardActionHint.IsVisible = !string.IsNullOrWhiteSpace(hint);
    }

    private void RenderReleaseChoices()
    {
        if (_vm is null)
        {
            return;
        }

        var stable = _vm.StableReleaseAvailable;
        var beta = _vm.BetaReleaseAvailable;
        StableChoiceButton.IsEnabled = stable;
        BetaChoiceButton.IsEnabled = beta;
        StableChoiceVersion.Text = stable ? _vm.LatestStableVersionText : Loc.Get("notAvailable");
        BetaChoiceVersion.Text = beta ? _vm.LatestBetaVersionText : Loc.Get("notAvailable");
        SetSelected(StableChoiceButton, _releaseChoice == "latest");
        SetSelected(BetaChoiceButton, _releaseChoice == "beta");
    }

    private static void SetSelected(Button button, bool selected)
    {
        if (selected)
        {
            if (!button.Classes.Contains("selected"))
            {
                button.Classes.Add("selected");
            }
        }
        else
        {
            button.Classes.Remove("selected");
        }
    }

    private async void GameAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _actionRunning)
        {
            return;
        }
        await RunActionAsync(() =>
        {
            _vm.RefreshLocalState();
            Render();
            return Task.CompletedTask;
        });
    }

    private async void BackendAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        await RunActionAsync(async () =>
        {
            await _vm.EnsureBackendForSetupAsync();
            Render();
        });
    }

    private async void DependenciesAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        await RunActionAsync(async () =>
        {
            await _vm.RunPreflightAsync();
            _vm.RefreshLocalState();
            Render();
        });
    }

    private async void RuntimeAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        await RunActionAsync(() =>
        {
            _vm.RefreshLocalState();
            Render();
            return Task.CompletedTask;
        });
    }

    private async void WiVrnAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        await RunActionAsync(async () =>
        {
            await _vm.RepairDesktopAsync();
            _vm.RefreshLocalState();
            Render();
        });
    }

    private async void SteamAction_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        await RunActionAsync(async () =>
        {
            await _vm.AddOrUpdateSteamAsync(allowRestart: false);
            _vm.RefreshLocalState();
            Render();
        });
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_vm is null || _actionRunning)
        {
            return;
        }
        _actionRunning = true;
        Render();
        try
        {
            await action();
        }
        finally
        {
            _actionRunning = false;
            Render();
        }
    }

    private void StableChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.StableReleaseAvailable)
        {
            return;
        }
        _releaseChoice = "latest";
        RenderReleaseChoices();
    }

    private void BetaChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.BetaReleaseAvailable)
        {
            return;
        }
        _releaseChoice = "beta";
        RenderReleaseChoices();
    }

    private void BuildSummary()
    {
        WizardSummaryRows.Children.Clear();
        if (_vm is null)
        {
            return;
        }

        AddSummaryRow(
            Loc.Get("gameInstallation"),
            _vm.GameDetected ? Loc.Get("detected") : Loc.Get("wizardNeedsAttention"),
            _vm.GameDetected ? _vm.TitanfallGameDirectory : Loc.Get("wizardGameMissingDetail"),
            _vm.GameDetected ? StatusKind.Ready : StatusKind.Attention);
        AddSummaryRow(
            Loc.Get("backendCheckout"),
            _vm.BackendVerified ? Loc.Get("wizardReady") : Loc.Get("wizardNeedsAttention"),
            _vm.BackendText,
            _vm.BackendVerified ? StatusKind.Ready : StatusKind.Attention);
        AddSummaryRow(
            Loc.Get("lastPreflight"),
            _vm.PreflightSucceeded
                ? Loc.Get("wizardReady")
                : _vm.PreflightHasRun ? Loc.Get("wizardNeedsAttention") : Loc.Get("notChecked"),
            _vm.PreflightHasRun ? _vm.LastPreflightText : Loc.Get("wizardDependenciesNotRunDetail"),
            _vm.PreflightSucceeded
                ? StatusKind.Ready
                : _vm.PreflightHasRun ? StatusKind.Attention : StatusKind.Neutral);
        AddSummaryRow(
            Loc.Get("activeOpenXrRuntime"),
            _vm.OpenXrDetected ? Loc.Get("wizardReady") : Loc.Get("wizardNeedsAttention"),
            _vm.OpenXrDetected ? _vm.OpenXrStatusText : Loc.Get("wizardOpenXrMissingDetail"),
            _vm.OpenXrDetected ? StatusKind.Ready : StatusKind.Attention);
        AddSummaryRow(
            Loc.Get("desktopEntry"),
            _vm.DesktopEntryInstalled
                ? Loc.Get("wizardReady")
                : _vm.WiVrnAvailable ? Loc.Get("wizardNotConfigured") : Loc.Get("notDetected"),
            null,
            _vm.DesktopEntryInstalled
                ? StatusKind.Ready
                : _vm.WiVrnAvailable ? StatusKind.Attention : StatusKind.Neutral);
        AddSummaryRow(
            Loc.Get("steamShortcutStatus"),
            _vm.SteamShortcutInstalled
                ? Loc.Get("wizardReady")
                : _vm.SteamDetected ? Loc.Get("wizardNotConfigured") : Loc.Get("notDetected"),
            null,
            _vm.SteamShortcutInstalled
                ? StatusKind.Ready
                : _vm.SteamDetected ? StatusKind.Attention : StatusKind.Neutral);

        var release = _vm.SelectedReleaseToken switch
        {
            "latest" => Loc.Get("stableLabel"),
            "beta" => Loc.Get("betaReleases"),
            _ => _vm.SelectedVersion?.DisplayLabel ?? Loc.Get("notAvailable"),
        };
        AddSummaryRow(
            Loc.Get("release"),
            release,
            _vm.SelectedVersionMeta,
            _vm.SelectedVersion is null ? StatusKind.Neutral : StatusKind.Ready);
    }

    private void AddSummaryRow(string label, string status, string? detail, StatusKind kind)
    {
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        heading.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var statusBlock = new TextBlock
        {
            Text = status,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushFor(kind switch
            {
                StatusKind.Ready => "Green",
                StatusKind.Attention => "Amber",
                StatusKind.Error => "Red",
                _ => "Muted",
            }),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(statusBlock, 1);
        heading.Children.Add(statusBlock);

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailBlock = new TextBlock
            {
                Text = detail,
                FontSize = 10.5,
                Foreground = BrushFor("TextDim"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(detailBlock, detail);
            content.Children.Add(detailBlock);
        }

        WizardSummaryRows.Children.Add(new Border
        {
            Background = BrushFor("Bg"),
            BorderBrush = BrushFor("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = Corner("CornerInner"),
            Padding = new Thickness(9, 5),
            Child = content,
        });
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

    private static CornerRadius Corner(string key)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            && value is CornerRadius radius)
        {
            return radius;
        }
        return new CornerRadius(6);
    }
}
