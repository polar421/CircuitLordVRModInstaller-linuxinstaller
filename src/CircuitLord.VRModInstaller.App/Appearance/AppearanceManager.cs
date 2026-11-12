using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Core.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;

namespace CircuitLord.VRModInstaller.Appearance;

/// <summary>
/// One place that owns the live appearance state. Views ask this coordinator
/// for the effective theme and subscribe to the two change notifications; no
/// view performs its own desktop detection or scale arithmetic.
/// </summary>
public static class AppearanceManager
{
    private static DispatcherTimer? _timer;

    public static ThemeService Theme { get; private set; } = new();

    public static MotionService Motion { get; private set; } = new();

    public static int ScalePercent { get; private set; } = UiScale.DefaultPercent;

    public static double ScaleFactor => UiScale.Factor(ScalePercent);

    public static event EventHandler? ScaleChanged;

    public static event EventHandler? MotionChanged;

    public static bool AnimationsEnabled => Motion.AnimationsEnabled;

    public static void Initialize(AppSettings settings)
    {
        Theme = new ThemeService();
        Motion = new MotionService();
        Theme.Initialize(ThemePreferenceNames.Parse(settings.Theme));
        Motion.Initialize(MotionPreferenceNames.Parse(settings.MotionPreference));
        ScalePercent = UiScale.ClampPercent(settings.UiScale);

        ApplyTheme();
        ApplyScaleResources();
        Theme.Changed += (_, _) => ApplyTheme();
        Motion.Changed += (_, _) => MotionChanged?.Invoke(null, EventArgs.Empty);

        StartPolling();
    }

    public static void SetTheme(ThemePreference preference)
    {
        if (Theme.Preference == preference)
        {
            return;
        }

        Theme.SetPreference(preference);
        ApplyTheme();
    }

    public static void SetMotion(MotionPreference preference)
    {
        Motion.SetPreference(preference);
        MotionChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SetScale(int percent)
    {
        var clamped = UiScale.ClampPercent(percent);
        if (ScalePercent == clamped)
        {
            return;
        }

        ScalePercent = clamped;
        ApplyScaleResources();
        ScaleChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyMotionClasses(Window? window)
    {
        if (window is null)
        {
            return;
        }

        window.Classes.Set("reduced-motion", Motion.IsReducedMotion);
    }

    private static void ApplyTheme()
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = Theme.EffectiveTheme == ResolvedTheme.Light
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;
    }

    private static void ApplyScaleResources()
    {
        if (Application.Current is null)
        {
            return;
        }

        var factor = ScaleFactor;
        Application.Current.Resources["UiDropdownFontSize"] = 12 * factor;
        Application.Current.Resources["UiDropdownPadding"] = new Thickness(10 * factor, 6 * factor);
        Application.Current.Resources["UiDropdownItemMinHeight"] = 28 * factor;
        Application.Current.Resources["UiTooltipFontSize"] = 11.5 * factor;
        Application.Current.Resources["UiTooltipPadding"] = new Thickness(8 * factor, 4 * factor);
    }

    private static void StartPolling()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) =>
        {
            if (Theme.RefreshSystemTheme())
            {
                ApplyTheme();
            }

            Motion.RefreshSystemMotion();
        };
        _timer.Start();
    }
}
