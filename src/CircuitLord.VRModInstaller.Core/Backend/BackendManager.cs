using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitLord.VRModInstaller.Core.Models;
using CircuitLord.VRModInstaller.Core.Process;

namespace CircuitLord.VRModInstaller.Core.Backend;

/// <summary>
/// Owns the application-managed checkout of Monkellie's tf2vr-linux.
///
/// Invariants:
///  - the origin must match the expected repository before any update;
///  - a failed clone never becomes the active checkout;
///  - the previous known-good commit is kept until the new one validates;
///  - no git command runs while an installer operation owns the lock
///    (the caller enforces that through the operation coordinator).
/// </summary>
public sealed class BackendManager
{
    public const string RepositoryUrl = "https://github.com/Monkellie/tf2vr-linux";
    public const string RepositoryUrlWithGit = RepositoryUrl + ".git";

    private static readonly string[] RequiredFiles = { "install.sh", "tf2vr", "tf2vr.desktop" };

    private readonly XdgPaths _paths;
    private readonly ProcessRunner _runner;

    public BackendManager(XdgPaths paths, ProcessRunner runner)
    {
        _paths = paths;
        _runner = runner;
    }

    public string CheckoutPath => _paths.BackendCheckout;

    /// <summary>Inspects the existing checkout without modifying anything.</summary>
    public async Task<BackendInstallation> InspectAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.BackendCheckout;
        if (!Directory.Exists(path) || !Directory.Exists(Path.Combine(path, ".git")))
        {
            return Missing(path, BackendValidationStatus.Missing);
        }

        var origin = await RunGitAsync(path, new[] { "remote", "get-url", "origin" }, cancellationToken);
        if (!origin.Succeeded || !IsExpectedOrigin(origin.StandardOutput.Trim()))
        {
            return Missing(path, BackendValidationStatus.OriginMismatch) with
            {
                CommitHash = await HeadAsync(path, cancellationToken),
            };
        }

        var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(path, f))).ToArray();
        var commit = await HeadAsync(path, cancellationToken);
        if (missing.Length > 0)
        {
            return Missing(path, BackendValidationStatus.Incomplete) with { CommitHash = commit };
        }

        var branch = await RunGitAsync(path, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, cancellationToken);

        return new BackendInstallation
        {
            RepositoryUrl = RepositoryUrl,
            CheckoutPath = path,
            VerifiedOrigin = true,
            CommitHash = commit,
            BranchName = branch.Succeeded ? branch.StandardOutput.Trim() : null,
            LastCheckedAt = DateTimeOffset.Now,
            ValidationStatus = BackendValidationStatus.Validated,
        };
    }

    /// <summary>
    /// Creates the checkout on first use. The clone lands in a temporary sibling
    /// directory and is only moved into place after validation succeeds.
    /// </summary>
    public async Task<BackendInstallation> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var existing = await InspectAsync(cancellationToken);
        if (existing.ValidationStatus == BackendValidationStatus.Validated)
        {
            return existing;
        }

        if (Directory.Exists(_paths.BackendCheckout))
        {
            // Never overwrite a checkout we cannot prove is ours.
            if (existing.ValidationStatus == BackendValidationStatus.OriginMismatch)
            {
                return existing;
            }
            Directory.Delete(_paths.BackendCheckout, recursive: true);
        }

        Directory.CreateDirectory(_paths.AppData);
        var partial = _paths.BackendCheckout + ".partial";
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        var clone = await _runner.RunAsync(
            "git",
            new[] { "clone", "--depth", "1", RepositoryUrl, partial },
            _paths.AppData,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        if (!clone.Succeeded)
        {
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }
            return Missing(_paths.BackendCheckout, BackendValidationStatus.CloneFailed) with
            {
                CommitHash = null,
            };
        }

        var validation = await ValidateCheckoutAsync(partial, cancellationToken);
        if (validation.ValidationStatus != BackendValidationStatus.Validated)
        {
            Directory.Delete(partial, recursive: true);
            return validation;
        }

        Directory.Move(partial, _paths.BackendCheckout);
        return await InspectAsync(cancellationToken);
    }

    /// <summary>
    /// Fetches the newest backend commit. The old commit stays checked out until
    /// the new one is validated; on failure the checkout is restored.
    /// </summary>
    public async Task<BackendUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var current = await InspectAsync(cancellationToken);
        if (current.ValidationStatus != BackendValidationStatus.Validated)
        {
            return new BackendUpdateResult
            {
                Succeeded = false,
                Message = "The backend checkout is not valid, so it cannot be updated safely.",
                Installation = current,
            };
        }

        var previousCommit = current.CommitHash;
        var fetch = await RunGitAsync(
            _paths.BackendCheckout,
            new[] { "fetch", "--depth", "1", "origin", "HEAD" },
            cancellationToken);

        if (!fetch.Succeeded)
        {
            return new BackendUpdateResult
            {
                Succeeded = false,
                Message = "Fetching the backend update failed: " + FirstLine(fetch.StandardError),
                Installation = current,
                PreviousCommit = previousCommit,
            };
        }

        var merge = await RunGitAsync(
            _paths.BackendCheckout,
            new[] { "merge", "--ff-only", "FETCH_HEAD" },
            cancellationToken);

        if (!merge.Succeeded)
        {
            await RunGitAsync(_paths.BackendCheckout, new[] { "merge", "--abort" }, cancellationToken);
            return new BackendUpdateResult
            {
                Succeeded = false,
                Message = "The backend update could not be applied cleanly: " + FirstLine(merge.StandardError),
                Installation = await InspectAsync(cancellationToken),
                PreviousCommit = previousCommit,
            };
        }

        var updated = await InspectAsync(cancellationToken);
        if (updated.ValidationStatus != BackendValidationStatus.Validated)
        {
            if (previousCommit is not null)
            {
                await RunGitAsync(_paths.BackendCheckout, new[] { "merge", "--ff-only", previousCommit }, cancellationToken);
            }
            return new BackendUpdateResult
            {
                Succeeded = false,
                Message = "The updated backend did not pass validation and was rolled back.",
                Installation = await InspectAsync(cancellationToken),
                PreviousCommit = previousCommit,
            };
        }

        return new BackendUpdateResult
        {
            Succeeded = true,
            Message = previousCommit == updated.CommitHash
                ? "The backend is already at the newest commit."
                : "Backend updated to " + ShortCommit(updated.CommitHash) + ".",
            Installation = updated,
            PreviousCommit = previousCommit,
        };
    }

    /// <summary>Reads the mod version recorded by install.sh (e.g. "mod=1.0.25 northstar=1.6.1").</summary>
    public string? ReadInstalledVersion()
    {
        try
        {
            return File.Exists(_paths.BackendVersionFile)
                ? File.ReadAllText(_paths.BackendVersionFile).Trim()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the backend's own saved selection token.</summary>
    public string? ReadSelectionToken()
    {
        try
        {
            if (!File.Exists(_paths.BackendSelectionFile))
            {
                return null;
            }
            var token = File.ReadAllText(_paths.BackendSelectionFile).Trim();
            return Catalogue.CatalogueParser.IsValidToken(token) ? token : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool IsExpectedOrigin(string origin)
    {
        var normalized = origin.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        return string.Equals(normalized, RepositoryUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, RepositoryUrlWithGit, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BackendInstallation> ValidateCheckoutAsync(string path, CancellationToken cancellationToken)
    {
        var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(path, f))).ToArray();
        if (missing.Length > 0)
        {
            return new BackendInstallation
            {
                RepositoryUrl = RepositoryUrl,
                CheckoutPath = path,
                ValidationStatus = BackendValidationStatus.Incomplete,
            };
        }

        var origin = await RunGitAsync(path, new[] { "remote", "get-url", "origin" }, cancellationToken);
        if (!origin.Succeeded || !IsExpectedOrigin(origin.StandardOutput.Trim()))
        {
            return new BackendInstallation
            {
                RepositoryUrl = RepositoryUrl,
                CheckoutPath = path,
                ValidationStatus = BackendValidationStatus.OriginMismatch,
            };
        }

        return new BackendInstallation
        {
            RepositoryUrl = RepositoryUrl,
            CheckoutPath = path,
            VerifiedOrigin = true,
            CommitHash = await HeadAsync(path, cancellationToken),
            LastCheckedAt = DateTimeOffset.Now,
            ValidationStatus = BackendValidationStatus.Validated,
        };
    }

    private async Task<string?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(path, new[] { "rev-parse", "HEAD" }, cancellationToken);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private Task<ProcessResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _runner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

    private static BackendInstallation Missing(string path, BackendValidationStatus status) => new()
    {
        RepositoryUrl = RepositoryUrl,
        CheckoutPath = path,
        ValidationStatus = status,
        LastCheckedAt = DateTimeOffset.Now,
    };

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "no details";

    private static string ShortCommit(string? commit) =>
        commit is { Length: >= 7 } ? commit[..7] : commit ?? "unknown";
}

public sealed class BackendUpdateResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public required BackendInstallation Installation { get; init; }
    public string? PreviousCommit { get; init; }
}
