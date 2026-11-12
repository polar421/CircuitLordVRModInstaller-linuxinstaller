using System;
using System.Collections.Generic;
using System.IO;
using CircuitLord.VRModInstaller.Core.Catalogue;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Backend;

/// <summary>
/// The single place that turns user intent into a backend invocation.
/// Verified against tf2vr-linux/install.sh (commit 8b02b87):
///   --check --list-versions --uninstall --force --refresh-proton
///   --version &lt;latest|beta|version&gt; --game DIR --prefix DIR --proton-base DIR
/// Arguments are passed as an array to bash; nothing is concatenated into a
/// shell string.
/// </summary>
public sealed class InstallScriptAdapter
{
    private readonly XdgPaths _paths;

    public InstallScriptAdapter(XdgPaths paths)
    {
        _paths = paths;
    }

    public BackendCapabilities GetCapabilities(BackendInstallation backend)
    {
        // The checked-out script is the authority. A partially cloned or invalid
        // checkout supports nothing.
        if (backend.ValidationStatus != BackendValidationStatus.Validated)
        {
            return new BackendCapabilities();
        }

        var script = SafeRead(backend.InstallerPath) ?? string.Empty;
        return new BackendCapabilities
        {
            SupportsCheck = script.Contains("--check", StringComparison.Ordinal),
            SupportsListVersions = script.Contains("--list-versions", StringComparison.Ordinal),
            SupportsVersionArgument = script.Contains("--version)", StringComparison.Ordinal),
            SupportsForce = script.Contains("--force)", StringComparison.Ordinal),
            SupportsRefreshProton = script.Contains("--refresh-proton", StringComparison.Ordinal),
            SupportsUninstall = script.Contains("--uninstall", StringComparison.Ordinal),
            SupportsManualGamePath = script.Contains("--game", StringComparison.Ordinal),
            SupportsManualPrefixPath = script.Contains("--prefix", StringComparison.Ordinal),
            SupportsProtonBase = script.Contains("--proton-base", StringComparison.Ordinal),
        };
    }

    public BackendCommand CreateListVersions(BackendInstallation backend) =>
        Create(backend, BackendOperationKind.ListVersions, "Reading the installable version catalogue", "--list-versions");

    public BackendCommand CreateCheck(BackendInstallation backend) =>
        Create(backend, BackendOperationKind.Check, "Running the backend preflight check", "--check");

    public BackendCommand CreateInstall(
        BackendInstallation backend,
        string token,
        bool force,
        bool refreshProton,
        string? gameDirectory = null,
        string? prefixDirectory = null)
    {
        if (!CatalogueParser.IsValidToken(token))
        {
            throw new ArgumentException("The release token is not valid.", nameof(token));
        }

        var args = new List<string> { "--version", token };
        if (force)
        {
            args.Add("--force");
        }
        if (refreshProton)
        {
            args.Add("--refresh-proton");
        }
        if (gameDirectory is not null)
        {
            args.Add("--game");
            args.Add(gameDirectory);
        }
        if (prefixDirectory is not null)
        {
            args.Add("--prefix");
            args.Add(prefixDirectory);
        }

        return Create(
            backend,
            force ? BackendOperationKind.Reinstall : BackendOperationKind.Install,
            $"Installing release {token} with the Linux backend",
            args.ToArray());
    }

    public BackendCommand CreateUninstall(BackendInstallation backend) =>
        Create(backend, BackendOperationKind.Uninstall, "Removing the mod with the backend uninstaller", "--uninstall");

    private BackendCommand Create(
        BackendInstallation backend,
        BackendOperationKind kind,
        string description,
        params string[] arguments)
    {
        if (backend.ValidationStatus != BackendValidationStatus.Validated)
        {
            throw new InvalidOperationException("The backend checkout has not been validated.");
        }
        if (!File.Exists(backend.InstallerPath))
        {
            throw new InvalidOperationException("install.sh is missing from the validated checkout.");
        }

        var args = new List<string> { backend.InstallerPath };
        args.AddRange(arguments);

        return new BackendCommand
        {
            OperationId = Guid.NewGuid().ToString("N"),
            ExecutablePath = ResolveBash(),
            WorkingDirectory = backend.CheckoutPath,
            Arguments = args,
            DisplayDescription = description,
            OperationKind = kind,
            // Installation downloads a 74 MB package and can build game assets.
            Timeout = kind switch
            {
                BackendOperationKind.Check => TimeSpan.FromMinutes(10),
                BackendOperationKind.ListVersions => TimeSpan.FromMinutes(3),
                BackendOperationKind.Uninstall => TimeSpan.FromMinutes(10),
                _ => TimeSpan.FromHours(2),
            },
        };
    }

    private static string ResolveBash() =>
        File.Exists("/usr/bin/bash") ? "/usr/bin/bash" : "/bin/bash";

    private static string? SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

public sealed class BackendCapabilities
{
    public bool SupportsCheck { get; init; }
    public bool SupportsListVersions { get; init; }
    public bool SupportsVersionArgument { get; init; }
    public bool SupportsForce { get; init; }
    public bool SupportsRefreshProton { get; init; }
    public bool SupportsUninstall { get; init; }
    public bool SupportsManualGamePath { get; init; }
    public bool SupportsManualPrefixPath { get; init; }
    public bool SupportsProtonBase { get; init; }
}
