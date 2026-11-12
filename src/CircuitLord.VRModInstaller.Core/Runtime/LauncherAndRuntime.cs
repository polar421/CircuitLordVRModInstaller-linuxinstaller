using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CircuitLord.VRModInstaller.Core.Runtime;

public sealed class LauncherLaunchResult
{
    public required bool Started { get; init; }
    public required string Message { get; init; }
    public int? ProcessId { get; init; }
}

/// <summary>
/// Launches Titanfall 2 VR through the installed native <c>tf2vr</c> launcher.
/// The same launcher is used by the Launch in VR button, the desktop entry and
/// the Steam shortcut; this service never reimplements the launch sequence.
/// </summary>
public sealed class LauncherService
{
    private readonly XdgPaths _paths;

    public LauncherService(XdgPaths paths)
    {
        _paths = paths;
    }

    public string LauncherPath => _paths.InstalledLauncher;

    public bool LauncherExists => File.Exists(LauncherPath);

    public bool IsExecutable
    {
        get
        {
            try
            {
                if (!LauncherExists)
                {
                    return false;
                }
                var mode = File.GetUnixFileMode(LauncherPath);
                return (mode & UnixFileMode.UserExecute) != 0;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Classifies the configuration file the launcher depends on.</summary>
    public bool HasBackendConfig => File.Exists(_paths.BackendPathsFile);

    public LauncherLaunchResult Launch(IReadOnlyList<string>? extraArguments = null)
    {
        if (!LauncherExists)
        {
            return new LauncherLaunchResult
            {
                Started = false,
                Message = "The tf2vr launcher is not installed. Install or repair Titanfall 2 VR first.",
            };
        }

        if (!IsExecutable)
        {
            return new LauncherLaunchResult
            {
                Started = false,
                Message = "The tf2vr launcher is not executable: " + LauncherPath,
            };
        }

        if (!HasBackendConfig)
        {
            return new LauncherLaunchResult
            {
                Started = false,
                Message = "The backend configuration is missing (" + _paths.BackendPathsFile + "). Run the installer again.",
            };
        }

        try
        {
            var psi = new ProcessStartInfo(LauncherPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(LauncherPath) ?? _paths.Home,
            };
            foreach (var argument in extraArguments ?? Array.Empty<string>())
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(psi);
            return new LauncherLaunchResult
            {
                Started = process is not null,
                ProcessId = process?.Id,
                Message = process is null
                    ? "The launcher did not start."
                    : "Titanfall 2 VR launch requested through tf2vr.",
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new LauncherLaunchResult
            {
                Started = false,
                Message = "The launcher could not start: " + ex.Message,
            };
        }
    }

    /// <summary>Opens the launcher's own log directory for support purposes.</summary>
    public string? LaunchLogPath => Path.Combine(_paths.StateHome, "tf2vr", "launch.log");
}

public sealed record OpenXrRuntimeInfo
{
    public string? RuntimeName { get; init; }
    public string? RuntimePath { get; init; }
    public string? ActiveRuntimeJsonPath { get; init; }
    public bool JsonFound { get; init; }
    public bool WiVrnRunning { get; init; }
    public bool MonadoRunning { get; init; }
    public bool SteamVrRunning { get; init; }
    public bool SteamVrAvailable { get; init; }
    public bool WiVrnAvailable { get; init; }
    public bool MonadoAvailable { get; init; }
    public string? SteamVrPath { get; init; }
    public string? OpenVrBridgePath { get; init; }
    public string? OpenVrBridgeKind { get; init; }
    public string? OpenVrPathsFile { get; init; }
    public string? ParseWarning { get; init; }

    /// <summary>Deliberately limited: a configured runtime is not a connected headset.</summary>
    public bool HeadsetConnected => false;

    public string Summary => !JsonFound
        ? "No active OpenXR runtime file was found (the launcher falls back to its own detection)."
        : (RuntimeName is null ? "Active runtime file found, runtime name unknown." : "Active OpenXR runtime: " + RuntimeName);
}

/// <summary>
/// Read-only OpenXR/WiVRn/Monado inspection. It never changes the active
/// runtime, never rewrites openvrpaths.vrpath and never forces SteamVR.
/// </summary>
public sealed class RuntimeService
{
    private readonly XdgPaths _paths;

    public RuntimeService(XdgPaths paths)
    {
        _paths = paths;
    }

    public string ActiveRuntimeJsonPath => Path.Combine(_paths.ConfigHome, "openxr", "1", "active_runtime.json");

    public OpenXrRuntimeInfo Inspect()
    {
        var processes = EnumerateCommandLines();
        var jsonPath = ActiveRuntimeJsonPath;
        var availability = DetectAvailability();

        if (!File.Exists(jsonPath))
        {
            return new OpenXrRuntimeInfo
            {
                ActiveRuntimeJsonPath = jsonPath,
                JsonFound = false,
                WiVrnRunning = processes.Any(p => p.Contains("wivrn", StringComparison.OrdinalIgnoreCase)),
                MonadoRunning = processes.Any(p => p.Contains("monado", StringComparison.OrdinalIgnoreCase)),
                SteamVrRunning = processes.Any(p => p.Contains("vrserver", StringComparison.OrdinalIgnoreCase)),
                SteamVrAvailable = availability.SteamVrAvailable,
                WiVrnAvailable = availability.WiVrnAvailable,
                MonadoAvailable = availability.MonadoAvailable,
                SteamVrPath = availability.SteamVrPath,
                OpenVrBridgePath = availability.OpenVrBridgePath,
                OpenVrBridgeKind = availability.OpenVrBridgeKind,
                OpenVrPathsFile = availability.OpenVrPathsFile,
            };
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = document.RootElement;
            string? name = null;
            string? path = null;
            if (root.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object)
            {
                if (runtime.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    name = n.GetString();
                }
                if (runtime.TryGetProperty("library_path", out var l) && l.ValueKind == JsonValueKind.String)
                {
                    path = l.GetString();
                }
            }

            return new OpenXrRuntimeInfo
            {
                ActiveRuntimeJsonPath = jsonPath,
                JsonFound = true,
                RuntimeName = name,
                RuntimePath = path,
                WiVrnRunning = processes.Any(p => p.Contains("wivrn", StringComparison.OrdinalIgnoreCase)),
                MonadoRunning = processes.Any(p => p.Contains("monado", StringComparison.OrdinalIgnoreCase)),
                SteamVrRunning = processes.Any(p => p.Contains("vrserver", StringComparison.OrdinalIgnoreCase)),
                SteamVrAvailable = availability.SteamVrAvailable,
                WiVrnAvailable = availability.WiVrnAvailable,
                MonadoAvailable = availability.MonadoAvailable,
                SteamVrPath = availability.SteamVrPath,
                OpenVrBridgePath = availability.OpenVrBridgePath,
                OpenVrBridgeKind = availability.OpenVrBridgeKind,
                OpenVrPathsFile = availability.OpenVrPathsFile,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new OpenXrRuntimeInfo
            {
                ActiveRuntimeJsonPath = jsonPath,
                JsonFound = true,
                ParseWarning = "The active runtime file could not be parsed: " + ex.Message,
                SteamVrAvailable = availability.SteamVrAvailable,
                WiVrnAvailable = availability.WiVrnAvailable,
                MonadoAvailable = availability.MonadoAvailable,
                SteamVrPath = availability.SteamVrPath,
                OpenVrBridgePath = availability.OpenVrBridgePath,
                OpenVrBridgeKind = availability.OpenVrBridgeKind,
                OpenVrPathsFile = availability.OpenVrPathsFile,
            };
        }
    }

    private RuntimeAvailability DetectAvailability()
    {
        var steamVrPath = new[]
        {
            Path.Combine(_paths.Home, ".local/share/Steam/steamapps/common/SteamVR"),
            Path.Combine(_paths.Home, ".steam/steam/steamapps/common/SteamVR"),
            Path.Combine(_paths.Home, ".steam/root/steamapps/common/SteamVR"),
        }.FirstOrDefault(p => File.Exists(Path.Combine(p, "bin/linux64/vrserver")));

        var wifiVrnAvailable = CommandExists("wivrn-server")
            || File.Exists(Path.Combine(_paths.Home, ".local/share/flatpak/app/io.github.wivrn.wivrn/current/active/files/bin/wivrn-server"))
            || File.Exists("/var/lib/flatpak/app/io.github.wivrn.wivrn/current/active/files/bin/wivrn-server");
        var monadoAvailable = CommandExists("monado-service");

        var openVrPathsFile = Path.Combine(_paths.ConfigHome, "openvr", "openvrpaths.vrpath");
        var candidates = new List<string>();
        if (File.Exists(openVrPathsFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(openVrPathsFile));
                if (document.RootElement.TryGetProperty("runtime", out var runtimes) && runtimes.ValueKind == JsonValueKind.Array)
                {
                    candidates.AddRange(runtimes.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var dataHome = _paths.DataHome;
        candidates.AddRange(new[]
        {
            Path.Combine(dataHome, "xrizer"),
            Path.Combine(dataHome, "OpenComposite"),
            Path.Combine(dataHome, "flatpak/app/io.github.wivrn.wivrn/current/active/files"),
            "/var/lib/flatpak/app/io.github.wivrn.wivrn/current/active/files",
            "/usr/lib/xrizer",
            "/usr/lib/OpenComposite",
            "/usr/lib64/xrizer",
            "/usr/local/lib/xrizer",
        });

        string? bridgePath = null;
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(candidate, "bin/linux64/vrclient.so"))
                && !File.Exists(Path.Combine(candidate, "bin/linux64/vrserver")))
            {
                bridgePath = candidate;
                break;
            }
        }

        var bridgeKind = bridgePath is null
            ? null
            : bridgePath.Contains("xrizer", StringComparison.OrdinalIgnoreCase) ? "xrizer"
            : bridgePath.Contains("OpenComposite", StringComparison.OrdinalIgnoreCase) ? "OpenComposite"
            : bridgePath.Contains("wivrn", StringComparison.OrdinalIgnoreCase) ? "WiVRn"
            : "Registered OpenVR runtime";

        return new RuntimeAvailability(
            steamVrPath is not null, wifiVrnAvailable, monadoAvailable,
            steamVrPath, bridgePath, bridgeKind, openVrPathsFile);
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, command))
            .Any(File.Exists);
    }

    private sealed record RuntimeAvailability(
        bool SteamVrAvailable,
        bool WiVrnAvailable,
        bool MonadoAvailable,
        string? SteamVrPath,
        string? OpenVrBridgePath,
        string? OpenVrBridgeKind,
        string? OpenVrPathsFile);

    private static List<string> EnumerateCommandLines()
    {
        var results = new List<string>();
        if (!Directory.Exists("/proc"))
        {
            return results;
        }
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == Environment.ProcessId)
            {
                continue;
            }
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, "cmdline"));
                if (bytes.Length > 0)
                {
                    results.Add(System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' '));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return results;
    }
}
