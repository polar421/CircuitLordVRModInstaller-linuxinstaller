using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Appearance;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class LayoutAuditTests : HeadlessUiTest
{
    private static readonly int[] Scales = [80, 100, 125, 150];
    private static readonly string[] Languages = ["en", "de", "es", "fr", "pt-BR", "it", "pl", "ru", "ja", "zh-Hans"];
    private static readonly string[] Themes = ["dark", "light", "system"];
    private static readonly (string Button, string Pane)[] SettingsTabs =
    [
        ("SettingsGeneralTabButton", "SettingsGeneralScroll"),
        ("SettingsAppearanceTabButton", "SettingsAppearanceScroll"),
        ("SettingsDiagnosticsTabButton", "SettingsDiagnosticsScroll"),
        ("SettingsAboutTabButton", "SettingsAboutScroll"),
    ];

    [Fact]
    public Task LayoutMatrixHasNoClippingOrOverflow() => Dispatch(() =>
    {
        var issues = new List<string>();
        try
        {
            foreach (var scale in Scales)
            {
                using var settings = new SettingsUiHost(
                    Home,
                    settings => settings.UiScale = scale,
                    width: 560,
                    height: 520);

                foreach (var language in Languages)
                {
                    Loc.Use(language);
                    HeadlessUi.Pump(100);
                    foreach (var theme in Themes)
                    {
                        AppearanceManager.SetTheme(ThemePreferenceNames.Parse(theme));
                        HeadlessUi.Pump(100);
                        foreach (var (button, pane) in SettingsTabs)
                        {
                            settings.ClickTab(button);
                            HeadlessUi.Pump(80);
                            AuditSurface(
                                settings.View,
                                $"Settings {language}/{theme}/{scale}%/{pane}",
                                issues);
                        }
                    }
                }
            }

            foreach (var scale in Scales)
            {
                foreach (var language in Languages)
                {
                    Loc.Use(language);
                    AppearanceManager.SetTheme(ThemePreference.Dark);
                    var view = new SetupWizardView();
                    var window = new Window
                    {
                        Width = 560,
                        Height = 520,
                        Content = new LayoutTransformControl
                        {
                            LayoutTransform = new ScaleTransform(
                                UiScale.Factor(scale),
                                UiScale.Factor(scale)),
                            Child = view,
                        },
                    };
                    try
                    {
                        window.Show();
                        var vm = new InstallerViewModel();
                        foreach (var theme in Themes)
                        {
                            AppearanceManager.SetTheme(ThemePreferenceNames.Parse(theme));
                            view.Open(vm);
                            view.RefreshAppearance();
                            HeadlessUi.Pump(120);
                            var primary = view.FindControl<Button>("WizardPrimaryButton")!;

                            for (var step = 0; step < 9; step++)
                            {
                                AuditSurface(
                                    view,
                                    $"Wizard {language}/{theme}/{scale}%/step{step + 1}",
                                    issues);
                                if (step < 8)
                                {
                                    primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                    HeadlessUi.Pump(80);
                                }
                            }

                            view.FindControl<Button>("WizardCloseButton")!
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            HeadlessUi.Pump(100);
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
        }
        finally
        {
            Loc.Use("en");
            AppearanceManager.SetTheme(ThemePreference.Dark);
            AppearanceManager.SetScale(UiScale.DefaultPercent);
        }

        if (issues.Count > 0)
        {
            var output = new StringBuilder();
            output.AppendLine($"Found {issues.Count} layout issue(s):");
            foreach (var issue in issues.Distinct().Take(300))
            {
                output.AppendLine(" - " + issue);
            }

            Assert.Fail(output.ToString());
        }
    });

    private static void AuditSurface(Control root, string context, List<string> issues)
    {
        var dialog = root.FindControl<Border>(
            context.StartsWith("Settings", StringComparison.Ordinal)
                ? "SettingsDialog"
                : "WizardDialog");
        if (dialog is null || !dialog.IsVisible)
        {
            issues.Add($"{context}: dialog is not visible");
            return;
        }

        var dialogParent = dialog.GetVisualParent() as Control;
        if (dialogParent is not null && dialogParent.Bounds.Width > 0)
        {
            if (dialog.Bounds.X < -1 || dialog.Bounds.Y < -1
                || dialog.Bounds.Right > dialogParent.Bounds.Width + 1
                || dialog.Bounds.Bottom > dialogParent.Bounds.Height + 1)
            {
                issues.Add($"{context}: dialog escapes its overlay ({dialog.Bounds}, overlay {dialogParent.Bounds.Size})");
            }
        }

        foreach (var scroll in root.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
        {
            if (scroll.Viewport.Width > 0 && scroll.Extent.Width > scroll.Viewport.Width + 1)
            {
                issues.Add(
                    $"{context}: {Describe(scroll)} has horizontal overflow " +
                    $"(extent {scroll.Extent.Width:0.#}, viewport {scroll.Viewport.Width:0.#})");
            }
        }

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text) || text.Bounds.Width <= 0)
            {
                continue;
            }

            if (text.TextTrimming == TextTrimming.None)
            {
                // DesiredSize includes the TextBlock margin; Bounds does not.
                var desiredWidth = text.DesiredSize.Width - text.Margin.Left - text.Margin.Right;
                var desiredHeight = text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom;
                if (text.Bounds.Width + 1 < desiredWidth)
                {
                    issues.Add(
                        $"{context}: {Describe(text)} is horizontally clipped " +
                        $"('{Short(text.Text)}', bounds {text.Bounds.Width:0.#}, desired {desiredWidth:0.#})");
                }

                if (text.Bounds.Height + 1 < desiredHeight)
                {
                    issues.Add(
                        $"{context}: {Describe(text)} is vertically clipped " +
                        $"('{Short(text.Text)}', bounds {text.Bounds.Height:0.#}, desired {desiredHeight:0.#})");
                }
            }

            var parent = text.GetVisualParent() as Control;
            if (parent is not null && parent.Bounds.Width > 0)
            {
                var child = text.Bounds;
                if (child.X < -1 || child.Right > parent.Bounds.Width + 1)
                {
                    issues.Add(
                        $"{context}: {Describe(text)} overflows {Describe(parent)} horizontally " +
                        $"('{Short(text.Text)}', child {child}, parent {parent.Bounds.Size})");
                }

                if (!HasScrollAncestor(text)
                    && (child.Y < -1 || child.Bottom > parent.Bounds.Height + 1))
                {
                    issues.Add(
                        $"{context}: {Describe(text)} overflows {Describe(parent)} vertically " +
                        $"('{Short(text.Text)}', child {child}, parent {parent.Bounds.Size})");
                }
            }
        }

        foreach (var button in root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            if (button.Bounds.Width + 1 < button.DesiredSize.Width)
            {
                issues.Add(
                    $"{context}: {Describe(button)} is horizontally clipped " +
                    $"(bounds {button.Bounds.Width:0.#}, desired {button.DesiredSize.Width:0.#})");
            }

            var parent = button.GetVisualParent() as Control;
            if (parent is not null && parent.Bounds.Width > 0)
            {
                var child = button.Bounds;
                if (child.X < -1 || child.Right > parent.Bounds.Width + 1)
                {
                    issues.Add(
                        $"{context}: {Describe(button)} overflows {Describe(parent)} horizontally " +
                        $"(child {child}, parent {parent.Bounds.Size})");
                }
            }
        }

        AuditFooter(root, context, issues);
    }

    private static void AuditFooter(Control root, string context, List<string> issues)
    {
        var footer = context.StartsWith("Settings", StringComparison.Ordinal)
            ? root.FindControl<Border>("SettingsFooter")
            : null;
        if (footer is not null)
        {
            var description = root.FindControl<TextBlock>("SettingsRestoreDescription");
            var button = root.FindControl<Button>("SettingsRestoreButton");
            if (description is not null && button is not null)
            {
                var d = description.Bounds;
                var b = button.Bounds;
                if (description.Bounds.Height + 1 < description.DesiredSize.Height)
                {
                    issues.Add($"{context}: restore description is vertically clipped");
                }

                if (button.Bounds.Width + 1 < button.DesiredSize.Width)
                {
                    issues.Add($"{context}: restore button is horizontally clipped");
                }

                if (d.Intersects(b))
                {
                    issues.Add($"{context}: restore description overlaps its button");
                }
            }
        }

        if (context.StartsWith("Wizard", StringComparison.Ordinal))
        {
            var skip = root.FindControl<Button>("WizardSkipSetupButton");
            var primary = root.FindControl<Button>("WizardPrimaryButton");
            if (skip is not null && primary is not null && skip.IsVisible)
            {
                var skipBounds = BoundsIn(skip, root);
                var primaryBounds = BoundsIn(primary, root);
                if (skipBounds.Intersects(primaryBounds))
                {
                    issues.Add($"{context}: wizard footer buttons overlap");
                }
            }
        }
    }

    private static bool HasScrollAncestor(Visual visual)
    {
        for (var current = visual.GetVisualParent(); current is not null; current = current.GetVisualParent())
        {
            if (current is ScrollViewer)
            {
                return true;
            }
        }

        return false;
    }

    private static Rect BoundsIn(Control control, Visual ancestor)
    {
        var origin = control.TranslatePoint(default, ancestor) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    private static string Describe(Control control)
    {
        var name = control.Name is { Length: > 0 } value ? "#" + value : string.Empty;
        var classes = control.Classes.Count > 0 ? "." + string.Join(".", control.Classes) : string.Empty;
        return control.GetType().Name + name + classes;
    }

    private static string Short(string value) =>
        value.Length <= 52 ? value : value[..49] + "...";
}
