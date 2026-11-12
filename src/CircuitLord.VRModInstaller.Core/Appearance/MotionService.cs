using System.Diagnostics;

namespace CircuitLord.VRModInstaller.Core.Appearance;

public enum MotionPreference
{
    System,
    Reduced,
    Full,
}

public sealed record SystemMotionResult(
    bool Detected,
    bool ReducedMotion,
    string Source,
    string? Detail = null);

public interface ISystemMotionDetector
{
    SystemMotionResult Detect();
}

/// <summary>
/// Detects Linux accessibility motion settings. KDE exposes the animation
/// duration factor, GNOME exposes enable-animations, and GTK environments can
/// disable animations in their settings files.
/// </summary>
public sealed class LinuxSystemMotionDetector : ISystemMotionDetector
{
    private readonly string _configHome;
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string, string[], string?> _run;

    public LinuxSystemMotionDetector(
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

    public SystemMotionResult Detect()
    {
        var kde = TryKde();
        if (kde is not null)
        {
            return kde;
        }

        var gnome = TryGnome();
        if (gnome is not null)
        {
            return gnome;
        }

        var gtk = TryGtk();
        if (gtk is not null)
        {
            return gtk;
        }

        return new SystemMotionResult(false, false, "Not detected");
    }

    private SystemMotionResult? TryKde()
    {
        var text = _readFile(Path.Combine(_configHome, "kdeglobals"));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (!section.Equals("KDE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Equals("AnimationDurationFactor", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var factor))
            {
                return new SystemMotionResult(
                    true,
                    factor <= 0,
                    "KDE animation duration",
                    factor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    private SystemMotionResult? TryGnome()
    {
        var reduce = (_run("gsettings", ["get", "org.gnome.desktop.a11y.interface", "reduce-animation"]) ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (reduce.StartsWith("true"))
        {
            return new SystemMotionResult(true, true, "GNOME reduce-animation", reduce);
        }

        var animations = (_run("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"]) ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (animations.Length > 0)
        {
            var reduced = !animations.StartsWith("true");
            return new SystemMotionResult(true, reduced, "GNOME enable-animations", animations);
        }

        return null;
    }

    private SystemMotionResult? TryGtk()
    {
        var env = _getEnv("GTK_ENABLE_ANIMATIONS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var reduced = env.Equals("0", StringComparison.OrdinalIgnoreCase)
                || env.Equals("false", StringComparison.OrdinalIgnoreCase);
            return new SystemMotionResult(true, reduced, "GTK_ENABLE_ANIMATIONS", env);
        }

        foreach (var version in new[] { "gtk-4.0", "gtk-3.0" })
        {
            var text = _readFile(Path.Combine(_configHome, version, "settings.ini"));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                if (line[..equals].Trim().Equals("gtk-enable-animations", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(equals + 1)..].Trim();
                    var reduced = value.Equals("false", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("0", StringComparison.OrdinalIgnoreCase);
                    return new SystemMotionResult(true, reduced, "GTK enable-animations", value);
                }
            }
        }

        return null;
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

public sealed class MotionService
{
    private readonly ISystemMotionDetector _detector;

    public MotionService(ISystemMotionDetector? detector = null)
    {
        _detector = detector ?? new LinuxSystemMotionDetector();
    }

    public MotionPreference Preference { get; private set; } = MotionPreference.System;

    public bool IsReducedMotion { get; private set; }

    public bool AnimationsEnabled => !IsReducedMotion;

    public SystemMotionResult SystemMotion { get; private set; } = new(false, false, "Not detected");

    public event EventHandler? Changed;

    public void Initialize(MotionPreference preference)
    {
        Preference = preference;
        Resolve();
    }

    public void SetPreference(MotionPreference preference)
    {
        Preference = preference;
        Resolve();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Refreshes automatic detection. Explicit Reduced/Full choices always win.
    /// Returns true when the effective motion state changed.
    /// </summary>
    public bool RefreshSystemMotion()
    {
        if (Preference != MotionPreference.System)
        {
            return false;
        }

        var before = IsReducedMotion;
        Resolve();
        if (before == IsReducedMotion)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Resolve()
    {
        SystemMotion = _detector.Detect();
        IsReducedMotion = Preference switch
        {
            MotionPreference.Reduced => true,
            MotionPreference.Full => false,
            _ => SystemMotion.Detected && SystemMotion.ReducedMotion,
        };
    }
}

public static class MotionPreferenceNames
{
    public static MotionPreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "reduced" => MotionPreference.Reduced,
        "full" => MotionPreference.Full,
        _ => MotionPreference.System,
    };

    public static string ToSetting(this MotionPreference preference) => preference switch
    {
        MotionPreference.Reduced => "reduced",
        MotionPreference.Full => "full",
        _ => "system",
    };
}
