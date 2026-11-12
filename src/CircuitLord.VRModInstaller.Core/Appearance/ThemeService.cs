using System.Diagnostics;

namespace CircuitLord.VRModInstaller.Core.Appearance;

/// <summary>The stored theme choice. Dark remains the application's default.</summary>
public enum ThemePreference
{
    System,
    Dark,
    Light,
}

/// <summary>The concrete theme that should be rendered.</summary>
public enum ResolvedTheme
{
    Dark,
    Light,
}

public sealed record SystemThemeResult(
    bool Detected,
    ResolvedTheme Theme,
    string Source,
    string? Detail = null);

public interface ISystemThemeDetector
{
    SystemThemeResult Detect();
}

/// <summary>
/// Detects the Linux desktop appearance without assuming a single mechanism.
/// KDE Plasma is read from kdeglobals, GNOME-family desktops from the supported
/// color-scheme preference, and other GTK environments from GTK settings.
/// </summary>
public sealed class LinuxSystemThemeDetector : ISystemThemeDetector
{
    private readonly string _configHome;
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string, string[], string?> _run;

    public LinuxSystemThemeDetector(
        string? configHome = null,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? readFile = null,
        Func<string, string[], string?>? run = null)
    {
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _configHome = configHome ?? ResolveConfigHome(_getEnv);
        _readFile = readFile ?? ReadFileOrNull;
        _run = run ?? RunCommand;
    }

    public SystemThemeResult Detect()
    {
        var desktop = (_getEnv("XDG_CURRENT_DESKTOP") ?? _getEnv("DESKTOP_SESSION") ?? string.Empty)
            .ToLowerInvariant();

        var kde = TryKde(desktop);
        if (kde is not null)
        {
            return kde;
        }

        var gnome = TryGnome(desktop);
        if (gnome is not null)
        {
            return gnome;
        }

        var gtk = TryGtk();
        if (gtk is not null)
        {
            return gtk;
        }

        return new SystemThemeResult(false, ResolvedTheme.Dark, "Fallback");
    }

    private SystemThemeResult? TryKde(string desktop)
    {
        var path = Path.Combine(_configHome, "kdeglobals");
        var text = _readFile(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var ini = ParseIni(text);
        if (ini.TryGetValue("General", out var general)
            && general.TryGetValue("ColorScheme", out var scheme)
            && !string.IsNullOrWhiteSpace(scheme))
        {
            var lower = scheme.Trim().ToLowerInvariant();
            if (lower.Contains("dark") || lower.Contains("night"))
            {
                return new SystemThemeResult(true, ResolvedTheme.Dark, "KDE Plasma color scheme", scheme.Trim());
            }

            if (lower.Contains("light") || lower == "breeze" || lower.Contains("white"))
            {
                return new SystemThemeResult(true, ResolvedTheme.Light, "KDE Plasma color scheme", scheme.Trim());
            }
        }

        if (ini.TryGetValue("Colors:Window", out var window)
            && window.TryGetValue("BackgroundNormal", out var background)
            && TryParseRgb(background, out var luminance))
        {
            return new SystemThemeResult(
                true,
                luminance < 0.5 ? ResolvedTheme.Dark : ResolvedTheme.Light,
                "KDE Plasma window colors",
                background.Trim());
        }

        return null;
    }

    private SystemThemeResult? TryGnome(string desktop)
    {
        var scheme = (_run("gsettings", ["get", "org.gnome.desktop.interface", "color-scheme"]) ?? string.Empty)
            .Trim()
            .Trim('\'')
            .ToLowerInvariant();

        if (scheme.Contains("prefer-dark") || scheme == "dark")
        {
            return new SystemThemeResult(true, ResolvedTheme.Dark, "GNOME color-scheme", scheme);
        }

        if (scheme.Contains("prefer-light") || scheme == "light")
        {
            return new SystemThemeResult(true, ResolvedTheme.Light, "GNOME color-scheme", scheme);
        }

        var gtkTheme = (_run("gsettings", ["get", "org.gnome.desktop.interface", "gtk-theme"]) ?? string.Empty)
            .Trim()
            .Trim('\'');
        var fromTheme = ThemeFromName(gtkTheme);
        if (fromTheme is not null)
        {
            return new SystemThemeResult(true, fromTheme.Value, "GTK theme", gtkTheme);
        }

        if (desktop.Contains("gnome") || desktop.Contains("unity") || desktop.Contains("cinnamon")
            || desktop.Contains("mate") || desktop.Contains("pantheon") || desktop.Contains("budgie"))
        {
            // GNOME was identified but its preference is the neutral "default".
            return new SystemThemeResult(true, ResolvedTheme.Light, "GNOME default appearance");
        }

        return null;
    }

    private SystemThemeResult? TryGtk()
    {
        var envTheme = _getEnv("GTK_THEME");
        var fromEnv = ThemeFromName(envTheme);
        if (fromEnv is not null)
        {
            return new SystemThemeResult(true, fromEnv.Value, "GTK_THEME", envTheme);
        }

        foreach (var version in new[] { "gtk-4.0", "gtk-3.0" })
        {
            var path = Path.Combine(_configHome, version, "settings.ini");
            var text = _readFile(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var ini = ParseIni(text);
            if (!ini.TryGetValue("Settings", out var settings))
            {
                continue;
            }

            if (settings.TryGetValue("gtk-application-prefer-dark-theme", out var preferDark)
                && IsTrue(preferDark))
            {
                return new SystemThemeResult(true, ResolvedTheme.Dark, "GTK prefer-dark setting");
            }

            if (settings.TryGetValue("gtk-theme-name", out var themeName))
            {
                var fromName = ThemeFromName(themeName);
                if (fromName is not null)
                {
                    return new SystemThemeResult(true, fromName.Value, "GTK theme", themeName);
                }
            }
        }

        return null;
    }

    private static ResolvedTheme? ThemeFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lower = name.ToLowerInvariant();
        if (lower.Contains("dark") || lower.Contains("night") || lower.Contains("black"))
        {
            return ResolvedTheme.Dark;
        }

        if (lower.Contains("light") || lower.Contains("white"))
        {
            return ResolvedTheme.Light;
        }

        return null;
    }

    private static bool TryParseRgb(string value, out double luminance)
    {
        luminance = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var r)
            || !int.TryParse(parts[1], out var g)
            || !int.TryParse(parts[2], out var b))
        {
            return false;
        }

        luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        return true;
    }

    private static bool IsTrue(string value)
        => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
           || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            if (!result.TryGetValue(section, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[section] = values;
            }

            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return result;
    }

    private static string ResolveConfigHome(Func<string, string?> getEnv)
    {
        var config = getEnv("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(config))
        {
            return config;
        }

        var home = getEnv("HOME");
        return string.IsNullOrWhiteSpace(home) ? string.Empty : Path.Combine(home, ".config");
    }

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? RunCommand(string fileName, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Detection must never keep startup alive.
                }

                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Resolves the stored preference against the current system appearance. The
/// detector is only consulted for System; an explicit Dark or Light choice is
/// never overwritten by a desktop change.
/// </summary>
public sealed class ThemeService
{
    private readonly ISystemThemeDetector _detector;

    public ThemeService(ISystemThemeDetector? detector = null)
    {
        _detector = detector ?? new LinuxSystemThemeDetector();
    }

    public ThemePreference Preference { get; private set; } = ThemePreference.Dark;

    public ResolvedTheme EffectiveTheme { get; private set; } = ResolvedTheme.Dark;

    public SystemThemeResult SystemTheme { get; private set; } = new(false, ResolvedTheme.Dark, "Not detected");

    public bool UsingFallback => Preference == ThemePreference.System && !SystemTheme.Detected;

    public event EventHandler? Changed;

    public void Initialize(ThemePreference preference)
    {
        Preference = preference;
        Resolve();
    }

    public bool SetPreference(ThemePreference preference)
    {
        var changed = Preference != preference;
        var before = EffectiveTheme;
        var beforeFallback = UsingFallback;
        Preference = preference;
        Resolve();
        var effectiveChanged = before != EffectiveTheme || beforeFallback != UsingFallback;
        if (changed || effectiveChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return effectiveChanged;
    }

    /// <summary>
    /// Re-reads the desktop appearance. Returns true when the rendered theme
    /// changed. Explicit preferences deliberately ignore the result.
    /// </summary>
    public bool RefreshSystemTheme()
    {
        if (Preference != ThemePreference.System)
        {
            return false;
        }

        var before = EffectiveTheme;
        Resolve();
        if (before == EffectiveTheme)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Resolve()
    {
        SystemTheme = _detector.Detect();
        EffectiveTheme = Preference switch
        {
            ThemePreference.Dark => ResolvedTheme.Dark,
            ThemePreference.Light => ResolvedTheme.Light,
            _ => SystemTheme.Detected ? SystemTheme.Theme : ResolvedTheme.Dark,
        };
    }
}

public static class ThemePreferenceNames
{
    public static ThemePreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "system" => ThemePreference.System,
        "light" => ThemePreference.Light,
        _ => ThemePreference.Dark,
    };

    public static string ToSetting(this ThemePreference preference) => preference switch
    {
        ThemePreference.System => "system",
        ThemePreference.Light => "light",
        _ => "dark",
    };
}
