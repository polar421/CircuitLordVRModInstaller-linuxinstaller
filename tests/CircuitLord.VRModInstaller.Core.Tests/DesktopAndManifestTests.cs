using System;
using System.IO;
using System.Linq;
using CircuitLord.VRModInstaller.Core.Desktop;
using CircuitLord.VRModInstaller.Core.Manifest;
using CircuitLord.VRModInstaller.Core.Models;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class DesktopEntryTests
{
    // The real tf2vr.desktop template from tf2vr-linux (commit 8b02b87).
    private const string BackendTemplate =
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Titanfall 2 VR\n" +
        "GenericName=Titanfall 2 (TF2VR mod)\n" +
        "Comment=Titanfall 2 campaign in VR with CircuitLord's TF2VR mod, through Proton-TF2VR\n" +
        "Exec=@BIN@\n" +
        "Icon=@ICON@\n" +
        "Terminal=false\n" +
        "Categories=Game;\n" +
        "Keywords=titanfall;vr;steamvr;openxr;proton;\n";

    [Fact]
    public void RequiredWiVrnCategoryReplacesTheBackendCategoryExactly()
    {
        using var env = new TempEnv();
        var manager = new DesktopEntryManager(env.Paths);

        var content = manager.BuildContent(BackendTemplate, env.Launcher, "applications-games");
        var lines = content.Split('\n');

        Assert.Contains(lines, l => l == "Categories=Game;X-WiVRn-VR;");
        Assert.DoesNotContain(lines, l => l == "Categories=Game;");
        Assert.Contains(lines, l => l == "Exec=" + env.Launcher);
        Assert.Contains(lines, l => l == "Terminal=false");
    }

    [Fact]
    public void InstallWritesTheCanonicalEntryToTheXdgApplicationsDirectory()
    {
        using var env = new TempEnv();
        var manager = new DesktopEntryManager(env.Paths);
        _ = env.Launcher; // the backend installs the launcher before the entry

        var status = manager.Install(BackendTemplate);

        Assert.True(File.Exists(env.Paths.DesktopEntryFile));
        Assert.Equal(env.Paths.DesktopEntryFile, status.DesktopFilePath);
        Assert.True(status.HasRequiredCategory);
        Assert.Equal(DesktopIntegrationState.Installed, status.State);
        Assert.True(status.LauncherExists);
    }

    [Fact]
    public void RepairRestoresACategoryLostByABackendUpdate()
    {
        using var env = new TempEnv();
        var manager = new DesktopEntryManager(env.Paths);
        manager.Install(BackendTemplate);

        // Simulate the backend regenerating its own template after an update.
        var regenerated = BackendTemplate
            .Replace("@BIN@", env.Launcher)
            .Replace("@ICON@", "applications-games");
        File.WriteAllText(env.Paths.DesktopEntryFile, regenerated);

        Assert.Equal(DesktopIntegrationState.CategoryMissing, manager.GetStatus().State);

        var repaired = manager.Repair();

        Assert.True(repaired.HasRequiredCategory);
        Assert.Equal(DesktopIntegrationState.Installed, repaired.State);
        Assert.Single(File.ReadAllLines(env.Paths.DesktopEntryFile), l => l.StartsWith("Categories="));
    }

    [Fact]
    public void MissingLauncherIsReportedInsteadOfClaimingSuccess()
    {
        using var env = new TempEnv();
        var manager = new DesktopEntryManager(env.Paths);
        manager.Install(BackendTemplate);
        File.Delete(env.Launcher);

        var status = manager.GetStatus();

        Assert.Equal(DesktopIntegrationState.LauncherMissing, status.State);
        Assert.False(status.LauncherExists);
    }

    [Fact]
    public void PathsWithSpacesAreCarriedIntoExecUnchanged()
    {
        using var env = new TempEnv();
        var manager = new DesktopEntryManager(env.Paths);
        var launcher = Path.Combine(env.Root, "my apps", "tf2vr");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(launcher, "#!/bin/bash\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var content = manager.BuildContent(BackendTemplate, launcher, "applications-games");

        Assert.Contains("Exec=" + launcher, content.Split('\n'));
    }
}

public class ManifestTests
{
    private const string ValidManifest = """
    {
      "schemaVersion": 3,
      "titanfall2vr": {
        "id": "Titanfall2VR",
        "name": "Titanfall 2 VR",
        "version": "1.0.25",
        "url": "https://example.invalid/Titanfall2VR-1.0.25.zip",
        "sha256": "e1f384f4",
        "size": 74001127,
        "channels": [
          { "id": "beta", "description": "New features, may be unstable", "version": "1.0.27" }
        ],
        "changes": [
          { "version": "1.0.25", "date": "2026-10-24", "notes": "Changes:\n- added support for ticks" }
        ]
      }
    }
    """;

    [Fact]
    public void ValidManifestIsParsedWithChannelsAndChangelog()
    {
        var service = new ManifestService(new XdgPaths(name => null, "/tmp"));
        var snapshot = service.Parse(ValidManifest, fromCache: false);

        Assert.NotNull(snapshot.Titanfall2Vr);
        Assert.Equal("1.0.25", snapshot.Titanfall2Vr!.Version);
        Assert.Single(snapshot.Titanfall2Vr.Channels);
        Assert.Equal("beta", snapshot.Titanfall2Vr.Channels[0].Id);
        Assert.Single(snapshot.Titanfall2Vr.Changes);
        Assert.Contains("ticks", snapshot.Titanfall2Vr.Changes[0].Notes);
        Assert.Null(snapshot.ValidationWarning);
    }

    [Fact]
    public void MissingProductIsAWarningNotAnException()
    {
        var service = new ManifestService(new XdgPaths(name => null, "/tmp"));
        var snapshot = service.Parse("{\"schemaVersion\":3}", fromCache: false);

        Assert.Null(snapshot.Titanfall2Vr);
        Assert.NotNull(snapshot.ValidationWarning);
    }

    [Fact]
    public void InvalidJsonIsReportedWithoutInventingMetadata()
    {
        var service = new ManifestService(new XdgPaths(name => null, "/tmp"));
        var snapshot = service.Parse("{ this is not json", fromCache: true);

        Assert.Null(snapshot.Titanfall2Vr);
        Assert.True(snapshot.IsFromCache);
        Assert.NotNull(snapshot.ValidationWarning);
    }

    [Fact]
    public void MalformedOptionalChangelogEntryDoesNotInvalidateTheSnapshot()
    {
        var json = ValidManifest.Replace(
            "\"changes\": [",
            "\"changes\": [ {\"version\": \"1.0.24\"}, ");

        var service = new ManifestService(new XdgPaths(name => null, "/tmp"));
        var snapshot = service.Parse(json, fromCache: false);

        Assert.NotNull(snapshot.Titanfall2Vr);
        Assert.Single(snapshot.Titanfall2Vr!.Changes);
    }
}
