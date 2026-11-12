using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Steam;

/// <summary>
/// One non-Steam shortcut inside <c>shortcuts.vdf</c>. Wraps the parsed map so
/// unknown fields written by Steam or other tools are preserved verbatim.
/// </summary>
public sealed class SteamShortcutEntry
{
    internal SteamShortcutEntry(string indexKey, VdfMap map)
    {
        IndexKey = indexKey;
        Map = map;
    }

    internal string IndexKey { get; set; }
    internal VdfMap Map { get; }

    public string Name => Map.GetString("AppName") ?? string.Empty;
    public string Executable => Map.GetString("Exe") ?? string.Empty;
    public string StartDirectory => Map.GetString("StartDir") ?? string.Empty;
    public string Icon => Map.GetString("icon") ?? string.Empty;
    public int AppId => Map.GetInt("appid") ?? 0;

    public bool LaunchesLauncher(string launcherPath) =>
        string.Equals(Executable, launcherPath, StringComparison.Ordinal)
        || Executable.EndsWith("/" + Path.GetFileName(launcherPath), StringComparison.Ordinal);
}

public enum ShortcutMatch
{
    None,
    Owned,
    Ambiguous,
}

/// <summary>
/// Parse / mutate / atomically write Steam's binary shortcut file while
/// preserving every unrelated entry and unknown key.
/// </summary>
public sealed class SteamShortcutsDocument
{
    public const string RootKey = "shortcuts";
    private readonly VdfMap _root;
    private readonly VdfMap _shortcuts;

    private SteamShortcutsDocument(VdfMap root, VdfMap shortcuts)
    {
        _root = root;
        _shortcuts = shortcuts;
    }

    public static SteamShortcutsDocument Empty()
    {
        var shortcuts = new VdfMap();
        var root = new VdfMap();
        root[RootKey] = shortcuts;
        return new SteamShortcutsDocument(root, shortcuts);
    }

    public static SteamShortcutsDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return Empty();
        }

        var root = BinaryVdf.Parse(bytes);
        var shortcuts = root.GetMap(RootKey)
            ?? throw new VdfFormatException("The shortcut file has no '" + RootKey + "' map.");

        return new SteamShortcutsDocument(root, shortcuts);
    }

    public IReadOnlyList<SteamShortcutEntry> Entries => _shortcuts.Entries
        .Where(e => e.Value is VdfMap)
        .Select(e => new SteamShortcutEntry(e.Key, (VdfMap)e.Value))
        .ToList();

    /// <summary>
    /// Identity is (name, launcher path). A name match with a different
    /// executable is a conflict for the caller to resolve, never a silent
    /// overwrite of somebody else's shortcut.
    /// </summary>
    public ShortcutMatch Match(string expectedName, string launcherPath, out IReadOnlyList<SteamShortcutEntry> matches)
    {
        var byName = Entries.Where(e => e.Name == expectedName).ToList();
        if (byName.Count == 0)
        {
            matches = Array.Empty<SteamShortcutEntry>();
            return ShortcutMatch.None;
        }

        var owned = byName.Where(e => e.LaunchesLauncher(launcherPath)).ToList();
        if (owned.Count == 1 && byName.Count == 1)
        {
            matches = owned;
            return ShortcutMatch.Owned;
        }
        if (owned.Count >= 1)
        {
            matches = owned;
            return ShortcutMatch.Owned;
        }

        matches = byName;
        return ShortcutMatch.Ambiguous;
    }

    /// <summary>Adds or updates exactly one entry, keeping every other shortcut.</summary>
    public SteamShortcutEntry Upsert(SteamShortcutRecord record)
    {
        var match = Match(record.Name, record.Executable, out var matches);
        if (match == ShortcutMatch.Ambiguous)
        {
            throw new InvalidOperationException(
                "A shortcut named '" + record.Name + "' exists but launches a different target; refusing to overwrite it.");
        }

        if (match == ShortcutMatch.Owned && matches.Count > 0)
        {
            var existing = matches[0];
            WriteFields(existing.Map, record);
            // Remove accidental duplicates we own, newest-first by index.
            foreach (var duplicate in matches.Skip(1).ToList())
            {
                RemoveEntry(duplicate);
            }
            return existing;
        }

        var nextIndex = NextIndex();
        var map = new VdfMap();
        WriteFields(map, record);
        _shortcuts[nextIndex] = map;
        return new SteamShortcutEntry(nextIndex, map);
    }

    public int RemoveOwned(string expectedName, string launcherPath)
    {
        var match = Match(expectedName, launcherPath, out var matches);
        if (match != ShortcutMatch.Owned)
        {
            return 0;
        }
        foreach (var entry in matches.ToList())
        {
            RemoveEntry(entry);
        }
        return matches.Count;
    }

    /// <summary>Atomically writes the document, fsyncs, then re-parses what landed on disk.</summary>
    public void SaveVerified(string path)
    {
        var bytes = BinaryVdf.Serialize(_root);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The shortcut file has no parent directory.");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Validate the temporary file before it replaces anything.
            var reparsed = BinaryVdf.Parse(File.ReadAllBytes(temp));
            if (reparsed.GetMap(RootKey) is null)
            {
                throw new VdfFormatException("The rewritten shortcut file failed verification.");
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        var committed = BinaryVdf.Parse(File.ReadAllBytes(path));
        if (committed.GetMap(RootKey) is null)
        {
            throw new VdfFormatException("The committed shortcut file could not be verified.");
        }
    }

    private void RemoveEntry(SteamShortcutEntry entry) => _shortcuts[entry.IndexKey] = null!;

    private string NextIndex()
    {
        var used = _shortcuts.Entries
            .Select(e => int.TryParse(e.Key, out var i) ? i : -1)
            .Where(i => i >= 0)
            .ToList();
        var next = used.Count == 0 ? 0 : used.Max() + 1;
        return next.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteFields(VdfMap map, SteamShortcutRecord record)
    {
        // Steam's documented non-Steam shortcut fields only; nothing is invented.
        map.Set("appid", record.AppId == 0 ? DeriveAppId(record.Executable, record.Name) : record.AppId);
        map.Set("AppName", record.Name);
        map.Set("Exe", record.Executable);
        map.Set("StartDir", record.StartDirectory);
        map.Set("icon", record.Icon);
        map.Set("ShortcutPath", string.Empty);
        map.Set("LaunchOptions", record.LaunchOptions);
        map.Set("IsHidden", 0);
        map.Set("AllowDesktopConfig", 1);
        map.Set("AllowOverlay", 1);
        map.Set("OpenVR", 0);
        map.Set("Devkit", 0);
        map.Set("DevkitGameID", string.Empty);
        map["LastPlayTime"] = new VdfInt(0);

        var tags = new VdfMap();
        for (var i = 0; i < record.Tags.Count; i++)
        {
            tags[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new VdfString(record.Tags[i]);
        }
        map["tags"] = tags;
    }

    /// <summary>
    /// Stable AppID in the format Steam uses for non-Steam shortcuts:
    /// CRC32 of executable + name with the high bit set. Stored as a signed
    /// int32 because that is how the VDF field is typed.
    /// </summary>
    public static int DeriveAppId(string executable, string name)
    {
        var crc = Crc32.Compute(executable + name) | 0x80000000u;
        return unchecked((int)crc);
    }
}
