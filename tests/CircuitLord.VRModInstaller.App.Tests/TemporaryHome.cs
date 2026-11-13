using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitLord.VRModInstaller.Core;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class TemporaryHome : IDisposable
{
    private static readonly string[] EnvironmentKeys =
    [
        "HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME",
    ];

    private readonly Dictionary<string, string?> _previousValues;

    public TemporaryHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "circuitlord-ui-tests-" + Guid.NewGuid().ToString("N"));
        HomeDirectory = Path.Combine(Root, "home");
        Directory.CreateDirectory(HomeDirectory);

        _previousValues = EnvironmentKeys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        Environment.SetEnvironmentVariable("HOME", HomeDirectory);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(HomeDirectory, ".config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(HomeDirectory, ".local", "share"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(HomeDirectory, ".cache"));
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(HomeDirectory, ".local", "state"));
    }

    public string Root { get; }

    public string HomeDirectory { get; }

    public XdgPaths Paths => new(home: HomeDirectory);

    public void Dispose()
    {
        foreach (var (key, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the actual test result.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the actual test result.
        }
    }
}
