using System;
using System.IO;
using CircuitLord.VRModInstaller.Core.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class AppearanceTests
{
    [Fact]
    public void KdeColorSchemeIsDetectedFromKdeglobals()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "kdeglobals"),
            """
            [General]
            ColorScheme=BreezeDark
            [Colors:Window]
            BackgroundNormal=35,38,41
            """);
        var detector = new LinuxSystemThemeDetector(
            configHome: temp.Path,
            getEnv: name => name == "XDG_CURRENT_DESKTOP" ? "KDE" : null,
            run: (_, _) => null);

        var result = detector.Detect();

        Assert.True(result.Detected);
        Assert.Equal(ResolvedTheme.Dark, result.Theme);
        Assert.Equal("KDE Plasma color scheme", result.Source);
    }

    [Fact]
    public void KdeWindowLuminanceIsUsedWhenTheSchemeNameIsCustom()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "kdeglobals"),
            """
            [General]
            ColorScheme=MyTheme
            [Colors:Window]
            BackgroundNormal=246,246,246
            """);
        var detector = new LinuxSystemThemeDetector(
            configHome: temp.Path,
            getEnv: _ => null,
            run: (_, _) => null);

        var result = detector.Detect();

        Assert.True(result.Detected);
        Assert.Equal(ResolvedTheme.Light, result.Theme);
        Assert.Equal("KDE Plasma window colors", result.Source);
    }

    [Fact]
    public void GnomeColorSchemePreferenceIsDetected()
    {
        var detector = new LinuxSystemThemeDetector(
            configHome: Path.GetTempPath(),
            getEnv: name => name == "XDG_CURRENT_DESKTOP" ? "GNOME" : null,
            run: (file, args) => file == "gsettings"
                && args.Contains("color-scheme")
                    ? "'prefer-light'"
                    : null);

        var result = detector.Detect();

        Assert.True(result.Detected);
        Assert.Equal(ResolvedTheme.Light, result.Theme);
        Assert.Contains("GNOME", result.Source);
    }

    [Fact]
    public void ThemeDetectionFallsBackToDarkWithoutThrowing()
    {
        var detector = new LinuxSystemThemeDetector(
            configHome: Path.GetTempPath(),
            getEnv: _ => null,
            run: (_, _) => null);

        var result = detector.Detect();

        Assert.False(result.Detected);
        Assert.Equal(ResolvedTheme.Dark, result.Theme);
    }

    [Fact]
    public void ExplicitThemeWinsOverSystemAndIsNotOverwritten()
    {
        var detector = new MutableThemeDetector(ResolvedTheme.Light);
        var service = new ThemeService(detector);

        service.Initialize(ThemePreference.Light);
        Assert.Equal(ResolvedTheme.Light, service.EffectiveTheme);

        detector.Theme = ResolvedTheme.Dark;
        Assert.False(service.RefreshSystemTheme());
        Assert.Equal(ResolvedTheme.Light, service.EffectiveTheme);
    }

    [Fact]
    public void SystemThemeTracksDesktopChangesAndUsesDarkFallback()
    {
        var detector = new MutableThemeDetector(ResolvedTheme.Light);
        var service = new ThemeService(detector);

        service.Initialize(ThemePreference.System);
        Assert.Equal(ResolvedTheme.Light, service.EffectiveTheme);
        Assert.False(service.UsingFallback);

        detector.Theme = ResolvedTheme.Dark;
        Assert.True(service.RefreshSystemTheme());
        Assert.Equal(ResolvedTheme.Dark, service.EffectiveTheme);

        detector.Detected = false;
        service.RefreshSystemTheme();
        Assert.True(service.UsingFallback);
        Assert.Equal(ResolvedTheme.Dark, service.EffectiveTheme);
    }

    [Fact]
    public void ThemeChangeEventFiresWhenSystemThemeChangesAndWhenPreferenceChanges()
    {
        var detector = new MutableThemeDetector(ResolvedTheme.Light);
        var service = new ThemeService(detector);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Initialize(ThemePreference.System);
        detector.Theme = ResolvedTheme.Dark;
        Assert.True(service.RefreshSystemTheme());
        Assert.True(changes > 0);

        var before = changes;
        service.SetPreference(ThemePreference.Light);
        Assert.True(changes > before);
        Assert.Equal(ResolvedTheme.Light, service.EffectiveTheme);
    }

    [Fact]
    public void MotionChangeEventFiresAndExplicitPreferenceWins()
    {
        var detector = new MutableMotionDetector(reduced: false);
        var service = new MotionService(detector);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Initialize(MotionPreference.System);
        detector.Reduced = true;
        Assert.True(service.RefreshSystemMotion());
        Assert.True(changes > 0);

        service.SetPreference(MotionPreference.Full);
        Assert.False(service.IsReducedMotion);
        Assert.False(service.RefreshSystemMotion());
    }

    [Fact]
    public void KdeAnimationDurationFactorZeroMeansReducedMotion()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "kdeglobals"),
            """
            [KDE]
            AnimationDurationFactor=0
            """);
        var detector = new LinuxSystemMotionDetector(
            configHome: temp.Path,
            getEnv: _ => null,
            run: (_, _) => null);

        var result = detector.Detect();

        Assert.True(result.Detected);
        Assert.True(result.ReducedMotion);
        Assert.Equal("KDE animation duration", result.Source);
    }

    [Fact]
    public void GnomeEnableAnimationsFalseMeansReducedMotion()
    {
        var detector = new LinuxSystemMotionDetector(
            configHome: Path.GetTempPath(),
            getEnv: _ => null,
            run: (file, args) => file == "gsettings" && args.Contains("enable-animations")
                ? "false"
                : null);

        var result = detector.Detect();

        Assert.True(result.Detected);
        Assert.True(result.ReducedMotion);
    }

    [Fact]
    public void ExplicitMotionPreferenceOverridesSystemDetection()
    {
        var detector = new MutableMotionDetector(reduced: true);
        var service = new MotionService(detector);

        service.Initialize(MotionPreference.Full);
        Assert.False(service.IsReducedMotion);

        service.SetPreference(MotionPreference.Reduced);
        Assert.True(service.IsReducedMotion);

        detector.Reduced = false;
        Assert.False(service.RefreshSystemMotion());
        Assert.True(service.IsReducedMotion);
    }

    [Fact]
    public void SystemMotionFollowsDetectedPreference()
    {
        var detector = new MutableMotionDetector(reduced: true);
        var service = new MotionService(detector);

        service.Initialize(MotionPreference.System);
        Assert.True(service.IsReducedMotion);

        detector.Reduced = false;
        Assert.True(service.RefreshSystemMotion());
        Assert.False(service.IsReducedMotion);
    }

    [Theory]
    [InlineData(80, 80)]
    [InlineData(83, 85)]
    [InlineData(100, 100)]
    [InlineData(148, 150)]
    [InlineData(10, 80)]
    [InlineData(150, 150)]
    public void UiScaleClampsAndSnapsToFivePercentSteps(double input, int expected)
    {
        Assert.Equal(expected, UiScale.ClampPercent(input));
    }

    [Theory]
    [InlineData(80, 0.8)]
    [InlineData(100, 1.0)]
    [InlineData(125, 1.25)]
    [InlineData(150, 1.5)]
    public void UiScaleFactorMatchesTheSelectedPercentage(int percent, double expected)
    {
        Assert.Equal(expected, UiScale.Factor(percent), 3);
    }

    private sealed class MutableThemeDetector : ISystemThemeDetector
    {
        public MutableThemeDetector(ResolvedTheme theme) => Theme = theme;

        public ResolvedTheme Theme { get; set; }

        public bool Detected { get; set; } = true;

        public SystemThemeResult Detect()
            => Detected
                ? new SystemThemeResult(true, Theme, "Test")
                : new SystemThemeResult(false, ResolvedTheme.Dark, "Not detected");
    }

    private sealed class MutableMotionDetector : ISystemMotionDetector
    {
        public MutableMotionDetector(bool reduced) => Reduced = reduced;

        public bool Reduced { get; set; }

        public SystemMotionResult Detect() => new(true, Reduced, "Test");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "circuitlord-appearance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }
}
