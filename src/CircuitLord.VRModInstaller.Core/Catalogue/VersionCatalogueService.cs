using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Backend;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Process;

namespace CircuitLord.VRModInstaller.Core.Catalogue;

/// <summary>
/// Runs <c>install.sh --list-versions</c> through the backend adapter and turns
/// its output into validated selectable entries. The manifest never decides
/// which tokens the backend accepts.
/// </summary>
public sealed class VersionCatalogueService
{
    private readonly XdgPaths _paths;
    private readonly ProcessRunner _runner;
    private readonly InstallScriptAdapter _adapter;
    private readonly OperationLoggerAccessor _logger;

    public VersionCatalogueService(
        XdgPaths paths,
        ProcessRunner runner,
        InstallScriptAdapter adapter,
        OperationLoggerAccessor? logger = null)
    {
        _paths = paths;
        _runner = runner;
        _adapter = adapter;
        _logger = logger ?? new OperationLoggerAccessor();
    }

    public async Task<VersionCatalogue> RefreshAsync(
        BackendInstallation backend,
        string? installedToken,
        CancellationToken cancellationToken = default)
    {
        var command = _adapter.CreateListVersions(backend);
        var result = await _runner.RunAsync(
            command.ExecutablePath,
            command.Arguments,
            command.WorkingDirectory,
            timeout: command.Timeout,
            onOutput: line => _logger.Log?.Invoke(line.Text),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var diagnostic = result.Started
                ? "install.sh --list-versions exited with code " + result.ExitCode +
                  (string.IsNullOrWhiteSpace(result.StandardError) ? "." : ": " + FirstLine(result.StandardError))
                : "The backend could not be started: " + result.StartFailure;
            var cached = LoadCache(diagnostic);
            if (cached is not null)
            {
                return cached;
            }
            return new VersionCatalogue
            {
                Entries = Array.Empty<VersionCatalogueEntry>(),
                RetrievedAt = DateTimeOffset.Now,
                Diagnostic = diagnostic,
            };
        }

        var catalogue = CatalogueParser.Parse(result.StandardOutput, installedToken, DateTimeOffset.Now);
        if (catalogue.Entries.Count > 0)
        {
            SaveCache(catalogue);
        }
        else if (LoadCache(catalogue.Diagnostic) is { } fallback)
        {
            return fallback;
        }

        return catalogue;
    }

    public VersionCatalogue? LoadCache(string? reason = null)
    {
        try
        {
            if (!File.Exists(_paths.CatalogueCacheFile))
            {
                return null;
            }
            var cached = JsonSerializer.Deserialize<VersionCatalogue>(File.ReadAllText(_paths.CatalogueCacheFile));
            if (cached is null || cached.Entries.Count == 0)
            {
                return null;
            }
            return new VersionCatalogue
            {
                Entries = cached.Entries,
                RetrievedAt = cached.RetrievedAt,
                IsFromCache = true,
                Diagnostic = reason is null ? "Cached catalogue." : reason + " Showing the last cached catalogue.",
                InstalledToken = cached.InstalledToken,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void SaveCache(VersionCatalogue catalogue)
    {
        try
        {
            Directory.CreateDirectory(_paths.AppCache);
            var temp = _paths.CatalogueCacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(catalogue, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _paths.CatalogueCacheFile, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
}

/// <summary>Small indirection so catalogue refreshes can report process output.</summary>
public sealed class OperationLoggerAccessor
{
    public Action<string>? Log { get; init; }
}
