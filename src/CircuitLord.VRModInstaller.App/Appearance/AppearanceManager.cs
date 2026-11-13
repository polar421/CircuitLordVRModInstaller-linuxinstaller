using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Core.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Appearance;

/// <summary>
/// One place that owns the live appearance state. Views ask this coordinator
/// for the effective theme and subscribe to the change notifications; no view
/// performs its own desktop detection, scale arithmetic or font discovery.
/// </summary>
public static class AppearanceManager
{
    private const string BundledFontPrefix = "avares://CircuitLordVRModInstaller/Assets/Fonts#";
    private const string LiberationSerifFamily = "Liberation Serif";
    private const string NotoSansCjkJpFamily = "Noto Sans CJK JP";
    private const string NotoSansCjkScFamily = "Noto Sans CJK SC";

    private static DispatcherTimer? _timer;
    private static bool _languageSubscribed;
    private static bool _fontDiscoveryFailed;
    private static IReadOnlyList<FontChoice> _availableFonts = FontPreference.BundledFonts;
    private static IReadOnlyList<FontChoice> _availableMonospaceFonts = Array.Empty<FontChoice>();
    private static FontChoice? _systemMonospaceFont;
    private static string _savedUiFontId = FontPreference.DefaultUiFontId;
    private static string _savedTechnicalFontId = FontPreference.FollowInterfaceFontId;

    public static ThemeService Theme { get; private set; } = new();

    public static MotionService Motion { get; private set; } = new();

    public static int ScalePercent { get; private set; } = UiScale.DefaultPercent;

    public static double ScaleFactor => UiScale.Factor(ScalePercent);

    public static event EventHandler? ScaleChanged;

    public static event EventHandler? MotionChanged;

    public static event EventHandler? FontChanged;

    public static bool AnimationsEnabled => Motion.AnimationsEnabled;

    public static IReadOnlyList<FontChoice> AvailableFonts => _availableFonts;

    public static IReadOnlyList<FontChoice> AvailableMonospaceFonts => _availableMonospaceFonts;

    /// <summary>The system's default monospace face, when one can be resolved.</summary>
    public static FontChoice? SystemMonospaceFont => _systemMonospaceFont;

    public static bool FontDiscoveryFailed => _fontDiscoveryFailed;

    /// <summary>The font actually rendered across the application.</summary>
    public static string UiFontId { get; private set; } = FontPreference.DefaultUiFontId;

    /// <summary>The technical font actually rendered, or <see cref="FontPreference.FollowInterfaceFontId"/>.</summary>
    public static string TechnicalFontId { get; private set; } = FontPreference.FollowInterfaceFontId;

    public static string? UnavailableUiFontId { get; private set; }

    public static string? UnavailableTechnicalFontId { get; private set; }

    public static void Initialize(AppSettings settings)
    {
        Theme = new ThemeService();
        Motion = new MotionService();
        Theme.Initialize(ThemePreferenceNames.Parse(settings.Theme));
        Motion.Initialize(MotionPreferenceNames.Parse(settings.MotionPreference));
        ScalePercent = UiScale.ClampPercent(settings.UiScale);
        _savedUiFontId = FontPreference.NormalizeUiFont(settings.UiFontFamily);
        _savedTechnicalFontId = FontPreference.NormalizeTechnicalFont(settings.TechnicalFontFamily);

        ApplyTheme();
        ApplyScaleResources();
        RefreshFontCatalog();
        Theme.Changed += (_, _) => ApplyTheme();
        Motion.Changed += (_, _) => MotionChanged?.Invoke(null, EventArgs.Empty);

        if (!_languageSubscribed)
        {
            _languageSubscribed = true;
            // The CJK fallback order follows the interface language, while the
            // user's selected primary family always stays first.
            Loc.LanguageChanged += (_, _) => ApplyResolvedFonts();
        }

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

    /// <summary>
    /// Selects a verified interface font. Returns false when the catalog no
    /// longer contains it, so callers do not persist a font that cannot load.
    /// </summary>
    public static bool SetUiFont(string fontId)
    {
        var normalized = FontPreference.NormalizeUiFont(fontId);
        var resolution = FontPreference.ResolveUiFont(normalized, FontIds(_availableFonts));
        if (resolution.IsUnavailable)
        {
            return false;
        }

        _savedUiFontId = resolution.EffectiveFontId;
        ApplyResolvedFonts();
        return true;
    }

    /// <summary>
    /// Selects a verified technical font, or <see cref="FontPreference.FollowInterfaceFontId"/>.
    /// Returns false when a requested font is not an available monospace face.
    /// </summary>
    public static bool SetTechnicalFont(string fontId)
    {
        var normalized = FontPreference.NormalizeTechnicalFont(fontId);
        var resolution = FontPreference.ResolveTechnicalFont(normalized, FontIds(_availableMonospaceFonts));
        if (resolution.IsUnavailable)
        {
            return false;
        }

        _savedTechnicalFontId = resolution.EffectiveFontId;
        ApplyResolvedFonts();
        return true;
    }

    /// <summary>
    /// Re-reads the font list Avalonia exposes. Called when Settings opens so an
    /// earlier discovery failure can recover; Avalonia caches its platform font
    /// list, so a font installed mid-session may need an application restart.
    /// </summary>
    public static void RefreshFontCatalog()
    {
        var catalog = DiscoverFontCatalog();
        _fontDiscoveryFailed = catalog.DiscoveryFailed;
        _availableFonts = catalog.Fonts;
        _availableMonospaceFonts = catalog.MonospaceFonts;
        _systemMonospaceFont = catalog.SystemMonospace;
        ApplyResolvedFonts();
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

    private static void ApplyResolvedFonts()
    {
        var ui = FontPreference.ResolveUiFont(_savedUiFontId, FontIds(_availableFonts));
        UiFontId = ui.EffectiveFontId;
        UnavailableUiFontId = ui.IsUnavailable ? ui.SavedFontId : null;

        var technical = FontPreference.ResolveTechnicalFont(
            _savedTechnicalFontId,
            FontIds(_availableMonospaceFonts));
        TechnicalFontId = technical.EffectiveFontId;
        UnavailableTechnicalFontId = technical.IsUnavailable ? technical.SavedFontId : null;

        if (Application.Current is null)
        {
            return;
        }

        var uiFamily = BuildFontFamily(UiFontId);
        Application.Current.Resources["UiFont"] = uiFamily;
        Application.Current.Resources["UiTechnicalFont"] =
            TechnicalFontId == FontPreference.FollowInterfaceFontId
                ? uiFamily
                : BuildFontFamily(TechnicalFontId);

        FontChanged?.Invoke(null, EventArgs.Empty);
    }

    private static IEnumerable<string> FontIds(IEnumerable<FontChoice> fonts)
        => fonts.Select(font => font.Id);

    private static FontFamily BuildFontFamily(string fontId)
    {
        var segments = new List<string>();
        var families = new List<string>();

        void Add(string? segment, string familyName)
        {
            if (string.IsNullOrEmpty(segment)
                || families.Any(name => string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            segments.Add(segment);
            families.Add(familyName);
        }

        if (FontPreference.TryGetBundledFamilyName(fontId, out var bundledName))
        {
            Add(BundledFontPrefix + bundledName, bundledName);
        }
        else if (FontPreference.TryGetSystemFamilyName(fontId, out var systemName))
        {
            Add(systemName, systemName);
        }
        else
        {
            Add(BundledFontPrefix + LiberationSerifFamily, LiberationSerifFamily);
        }

        // Liberation covers Latin, German, Polish and Cyrillic when the chosen
        // font is missing a character. The CJK collections cover Japanese and
        // Simplified Chinese. None of these replace the primary family.
        Add(BundledFontPrefix + LiberationSerifFamily, LiberationSerifFamily);
        if (Loc.Language == "zh-Hans")
        {
            Add(BundledFontPrefix + NotoSansCjkScFamily, NotoSansCjkScFamily);
            Add(BundledFontPrefix + NotoSansCjkJpFamily, NotoSansCjkJpFamily);
        }
        else
        {
            Add(BundledFontPrefix + NotoSansCjkJpFamily, NotoSansCjkJpFamily);
            Add(BundledFontPrefix + NotoSansCjkScFamily, NotoSansCjkScFamily);
        }

        try
        {
            return new FontFamily(string.Join(", ", segments));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed system family name must not take the UI down.
            return new FontFamily(BundledFontPrefix + LiberationSerifFamily);
        }
    }

    private static FontCatalog DiscoverFontCatalog()
    {
        var bundled = new List<FontChoice>();
        var system = new List<FontChoice>();
        var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveryFailed = false;

        // The bundled default must always be offered even if font discovery fails.
        bundled.Add(FontPreference.BundledFonts[0]);
        seenFamilies.Add(LiberationSerifFamily);

        FontManager manager;
        try
        {
            manager = FontManager.Current;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new FontCatalog(bundled, Array.Empty<FontChoice>(), null, DiscoveryFailed: true);
        }

        foreach (var choice in FontPreference.BundledFonts.Skip(1))
        {
            if (TryGetBundledChoice(manager, choice, out var validated))
            {
                bundled.Add(validated);
                seenFamilies.Add(validated.DisplayName);
            }
        }

        try
        {
            foreach (var family in manager.SystemFonts)
            {
                var familyName = family.Name?.Trim();
                if (string.IsNullOrWhiteSpace(familyName)
                    || familyName == FontFamily.DefaultFontFamilyName
                    || familyName.Contains(',')
                    || familyName.Contains('#')
                    || seenFamilies.Contains(familyName))
                {
                    continue;
                }

                try
                {
                    if (!TryGetValidatedGlyph(manager, family, familyName, out var glyph))
                    {
                        continue;
                    }

                    seenFamilies.Add(familyName);
                    system.Add(new FontChoice(
                        FontPreference.SystemFontId(familyName),
                        familyName,
                        IsMonospace(glyph),
                        IsBundled: false));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One unreadable family must not hide the rest of the catalog.
                }
            }

            system.Sort((left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));

            var all = bundled.Concat(system).ToList();
            var systemMonospace = ResolveSystemMonospace(manager, all);
            if (systemMonospace is not null
                && !all.Any(font => string.Equals(font.Id, systemMonospace.Id, StringComparison.OrdinalIgnoreCase)))
            {
                all.Add(systemMonospace);
            }

            var monospace = all
                .Where(font => font.IsMonospace)
                .OrderBy(font => font.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new FontCatalog(
                all,
                monospace,
                systemMonospace ?? monospace.FirstOrDefault(),
                discoveryFailed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            discoveryFailed = true;
        }

        return new FontCatalog(bundled, Array.Empty<FontChoice>(), null, discoveryFailed);
    }

    private static bool TryGetBundledChoice(FontManager manager, FontChoice choice, out FontChoice validated)
    {
        validated = choice;
        try
        {
            var family = new FontFamily(BundledFontPrefix + choice.DisplayName);
            if (!TryGetValidatedGlyph(manager, family, choice.DisplayName, out _))
            {
                return false;
            }

            validated = choice;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static FontChoice? ResolveSystemMonospace(FontManager manager, IReadOnlyList<FontChoice> all)
    {
        try
        {
            var typeface = new Typeface(new FontFamily("monospace"));
            if (!manager.TryGetGlyphTypeface(typeface, out var glyph)
                || string.IsNullOrWhiteSpace(glyph.FamilyName)
                || glyph.FamilyName.Contains(',')
                || glyph.FamilyName.Contains('#')
                || string.Equals(glyph.FamilyName, manager.DefaultFontFamily.Name, StringComparison.OrdinalIgnoreCase)
                || !IsMonospace(glyph))
            {
                return null;
            }

            return all.FirstOrDefault(font =>
                       string.Equals(font.DisplayName, glyph.FamilyName, StringComparison.OrdinalIgnoreCase))
                   ?? new FontChoice(
                       FontPreference.SystemFontId(glyph.FamilyName),
                       glyph.FamilyName,
                       IsMonospace: true,
                       IsBundled: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool TryGetValidatedGlyph(
        FontManager manager,
        FontFamily family,
        string expectedFamilyName,
        out GlyphTypeface glyph)
    {
        glyph = null!;
        if (!manager.TryGetGlyphTypeface(new Typeface(family), out var loaded)
            || loaded is null
            || !FamilyMatches(loaded, expectedFamilyName))
        {
            return false;
        }

        glyph = loaded;
        return true;
    }

    private static bool FamilyMatches(GlyphTypeface glyph, string expectedFamilyName)
    {
        if (string.Equals(glyph.FamilyName, expectedFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return glyph.FamilyNames.Values.Any(name =>
            string.Equals(name, expectedFamilyName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMonospace(GlyphTypeface glyph)
    {
        ushort? advanceWidth = null;
        foreach (var character in "ilMW0 .")
        {
            if (!glyph.CharacterToGlyphMap.TryGetGlyph(character, out var glyphId)
                || !glyph.TryGetHorizontalGlyphAdvance(glyphId, out var advance)
                || advance == 0)
            {
                return false;
            }

            if (advanceWidth is null)
            {
                advanceWidth = advance;
            }
            else if (advanceWidth != advance)
            {
                return false;
            }
        }

        return true;
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

    private sealed record FontCatalog(
        IReadOnlyList<FontChoice> Fonts,
        IReadOnlyList<FontChoice> MonospaceFonts,
        FontChoice? SystemMonospace,
        bool DiscoveryFailed);
}
