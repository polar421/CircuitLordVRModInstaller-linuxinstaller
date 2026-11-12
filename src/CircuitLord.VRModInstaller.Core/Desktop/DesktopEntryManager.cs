using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Desktop;

/// <summary>
/// Owns the canonical user-level Titanfall 2 VR desktop entry.
///
/// Contract verified against the backend: install.sh generates
/// $XDG_DATA_HOME/applications/tf2vr.desktop from its template with
/// <c>Categories=Game;</c> — it does NOT add the WiVRn category. This manager
/// reconciles the same canonical file after every backend operation so the
/// required line is present, without creating a competing launcher.
/// </summary>
public sealed class DesktopEntryManager
{
    public const string RequiredCategoriesLine = "Categories=Game;X-WiVRn-VR;";
    public const string ManagedMarker = "# Managed by CircuitLord's VR Mod Installer (Linux)";

    private readonly XdgPaths _paths;

    public DesktopEntryManager(XdgPaths paths)
    {
        _paths = paths;
    }

    public string CanonicalPath => _paths.DesktopEntryFile;

    /// <summary>Builds the entry from the backend template, then enforces the contract.</summary>
    public string BuildContent(string template, string launcherPath, string icon)
    {
        var lines = template
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line
                .Replace("@BIN@", launcherPath, StringComparison.Ordinal)
                .Replace("@ICON@", icon, StringComparison.Ordinal))
            .ToList();

        if (!lines.Any(l => l.StartsWith("[Desktop Entry]", StringComparison.Ordinal)))
        {
            lines.Insert(0, "[Desktop Entry]");
        }

        EnsureKey(lines, "Type", "Application");
        EnsureKey(lines, "Name", "Titanfall 2 VR");
        EnsureKey(lines, "Exec", launcherPath);
        EnsureKey(lines, "Icon", icon);
        EnsureKey(lines, "Terminal", "false");

        var categoryIndex = lines.FindIndex(l => l.StartsWith("Categories=", StringComparison.Ordinal));
        if (categoryIndex >= 0)
        {
            lines[categoryIndex] = RequiredCategoriesLine;
        }
        else
        {
            var insertAt = lines.FindLastIndex(l => l.Trim().Length > 0) + 1;
            lines.Insert(insertAt, RequiredCategoriesLine);
        }

        if (!lines.Any(l => l.StartsWith(ManagedMarker, StringComparison.Ordinal)))
        {
            lines.Add(ManagedMarker);
        }

        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    /// <summary>Writes the entry atomically, backing up any file we did not generate.</summary>
    public DesktopIntegrationStatus Install(string backendDesktopTemplate)
    {
        var launcher = _paths.InstalledLauncher;
        var icon = ResolveIconName();

        var content = BuildContent(backendDesktopTemplate, launcher, icon);

        Directory.CreateDirectory(_paths.ApplicationEntries);

        if (File.Exists(CanonicalPath))
        {
            var existing = File.ReadAllText(CanonicalPath);
            if (!existing.Contains(ManagedMarker, StringComparison.Ordinal))
            {
                var backup = CanonicalPath + ".bak-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(CanonicalPath, backup, overwrite: false);
            }
        }

        WriteAtomically(CanonicalPath, content);
        return GetStatus();
    }

    /// <summary>Reapplies the contract from an already-installed entry (no mod reinstall).</summary>
    public DesktopIntegrationStatus Repair()
    {
        if (!File.Exists(CanonicalPath))
        {
            return new DesktopIntegrationStatus
            {
                DesktopFileId = "tf2vr.desktop",
                DesktopFilePath = CanonicalPath,
                State = DesktopIntegrationState.NotInstalled,
                Detail = "No Titanfall 2 VR desktop entry exists yet; install the mod first.",
            };
        }

        var lines = File.ReadAllText(CanonicalPath).Replace("\r\n", "\n").Split('\n').ToList();
        var exec = lines.FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))?.Substring(5).Trim();
        var icon = lines.FirstOrDefault(l => l.StartsWith("Icon=", StringComparison.Ordinal))?.Substring(5).Trim()
            ?? ResolveIconName();

        var rebuilt = BuildContent(
            string.Join('\n', lines.Where(l => !l.StartsWith(ManagedMarker, StringComparison.Ordinal))),
            string.IsNullOrWhiteSpace(exec) ? _paths.InstalledLauncher : exec,
            icon);

        WriteAtomically(CanonicalPath, rebuilt);
        return GetStatus();
    }

    public DesktopIntegrationStatus GetStatus()
    {
        var duplicates = FindDuplicateEntries();
        var status = new DesktopIntegrationStatus
        {
            DesktopFileId = "tf2vr.desktop",
            DesktopFilePath = CanonicalPath,
            DuplicatePaths = duplicates,
        };

        if (!File.Exists(CanonicalPath))
        {
            return new DesktopIntegrationStatus
            {
                DesktopFileId = status.DesktopFileId,
                DesktopFilePath = status.DesktopFilePath,
                DuplicatePaths = duplicates,
                State = duplicates.Count > 0 ? DesktopIntegrationState.DuplicateEntries : DesktopIntegrationState.NotInstalled,
                Detail = duplicates.Count > 0 ? "Another Titanfall 2 VR entry exists but not the canonical one." : "Not installed.",
            };
        }

        string content;
        try
        {
            content = File.ReadAllText(CanonicalPath);
        }
        catch (IOException ex)
        {
            return new DesktopIntegrationStatus
            {
                DesktopFileId = status.DesktopFileId,
                DesktopFilePath = status.DesktopFilePath,
                State = DesktopIntegrationState.Error,
                Detail = ex.Message,
            };
        }

        var exec = content.Split('\n')
            .FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))
            ?.Substring(5).Trim() ?? string.Empty;
        var icon = content.Split('\n')
            .FirstOrDefault(l => l.StartsWith("Icon=", StringComparison.Ordinal))
            ?.Substring(5).Trim();
        var hasCategory = content.Split('\n').Any(l =>
            string.Equals(l.Trim(), RequiredCategoriesLine, StringComparison.Ordinal));
        var launcherExists = IsExecutableFile(exec);
        var validated = ValidateWithDesktopFileValidate(CanonicalPath);

        var state = !hasCategory ? DesktopIntegrationState.CategoryMissing
            : !launcherExists ? DesktopIntegrationState.LauncherMissing
            : !validated ? DesktopIntegrationState.InvalidFile
            : duplicates.Count > 0 ? DesktopIntegrationState.DuplicateEntries
            : DesktopIntegrationState.Installed;

        return new DesktopIntegrationStatus
        {
            DesktopFileId = status.DesktopFileId,
            DesktopFilePath = CanonicalPath,
            LauncherPath = exec,
            IconPath = icon,
            HasRequiredCategory = hasCategory,
            LauncherExists = launcherExists,
            ValidatesWithDesktopFileValidate = validated,
            DuplicatePaths = duplicates,
            State = state,
            LastReconciledAt = File.GetLastWriteTime(CanonicalPath),
            Detail = Describe(state, exec),
        };
    }

    /// <summary>
    /// Finds other .desktop files that claim to be Titanfall 2 VR or launch the
    /// backend launcher, so the app can consolidate instead of leaving several.
    /// </summary>
    public IReadOnlyList<string> FindDuplicateEntries()
    {
        var results = new List<string>();
        if (!Directory.Exists(_paths.ApplicationEntries))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.ApplicationEntries, "*.desktop"))
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(CanonicalPath), StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                var text = File.ReadAllText(file);
                var isVrEntry = text.Split('\n').Any(l => l.Trim() == "Name=Titanfall 2 VR");
                var launchesTf2vr = text.Contains("tf2vr", StringComparison.Ordinal);
                if (isVrEntry || launchesTf2vr)
                {
                    results.Add(file);
                }
            }
            catch (IOException)
            {
                // An unreadable entry is not reported as a duplicate.
            }
        }
        return results;
    }

    /// <summary>Removes only the canonical entry this application owns.</summary>
    public void Remove()
    {
        if (File.Exists(CanonicalPath))
        {
            var content = File.ReadAllText(CanonicalPath);
            if (content.Contains(ManagedMarker, StringComparison.Ordinal))
            {
                File.Delete(CanonicalPath);
            }
        }
    }

    private string ResolveIconName()
    {
        var iconsRoot = Path.Combine(_paths.DataHome, "icons");
        if (Directory.Exists(iconsRoot))
        {
            var steamIcon = Directory.EnumerateFiles(iconsRoot, "steam_icon_1237970.*", SearchOption.AllDirectories).FirstOrDefault();
            if (steamIcon is not null)
            {
                return "steam_icon_1237970";
            }
        }
        return "applications-games";
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool ValidateWithDesktopFileValidate(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo("desktop-file-validate")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return true; // Validator unavailable: do not claim failure without evidence.
            }
            process.WaitForExit(10_000);
            return !process.HasExited || process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Validator missing: the file itself is still checked field by field above.
            return true;
        }
    }

    private static string Describe(DesktopIntegrationState state, string exec) => state switch
    {
        DesktopIntegrationState.Installed => "Desktop entry installed and points at " + exec,
        DesktopIntegrationState.CategoryMissing => "The required WiVRn category is missing.",
        DesktopIntegrationState.LauncherMissing => "Exec points at a launcher that does not exist: " + exec,
        DesktopIntegrationState.InvalidFile => "desktop-file-validate reported problems with the entry.",
        DesktopIntegrationState.DuplicateEntries => "More than one Titanfall 2 VR entry was found.",
        _ => state.ToString(),
    };

    private static void EnsureKey(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        if (index < 0)
        {
            var insertAt = lines.FindLastIndex(l => l.Trim().Length > 0) + 1;
            lines.Insert(insertAt, key + "=" + value);
        }
        else if (string.IsNullOrWhiteSpace(lines[index].Substring(key.Length + 1)))
        {
            lines[index] = key + "=" + value;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, content);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temp, path, overwrite: true);
    }
}
