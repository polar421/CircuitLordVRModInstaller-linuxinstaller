using System;
using System.IO;
using CircuitLord.VRModInstaller.Core.Notifications;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class NotificationStoreTests
{
    [Fact]
    public void ActivityRoundTripsWithStructuredFields()
    {
        using var env = new TempEnv();
        var store = new NotificationStore(env.Paths);
        var created = DateTimeOffset.Parse("2026-11-12T09:00:00+00:00");
        store.Save(new[]
        {
            new AppNotification
            {
                Id = "install-1",
                Kind = NotificationKinds.InstallCompleted,
                CreatedAt = created,
                Version = "1.0.27",
                DedupeKey = "mod-beta:1.0.27",
            },
            new AppNotification
            {
                Id = "failure-1",
                Kind = NotificationKinds.InstallFailed,
                CreatedAt = created.AddMinutes(-5),
                Version = "1.0.25",
                Detail = "1",
                Read = true,
            },
        });

        var loaded = new NotificationStore(env.Paths).Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(NotificationKinds.InstallCompleted, loaded[0].Kind);
        Assert.Equal("1.0.27", loaded[0].Version);
        Assert.Equal("mod-beta:1.0.27", loaded[0].DedupeKey);
        Assert.False(loaded[0].Read);
        Assert.Equal(NotificationKinds.InstallFailed, loaded[1].Kind);
        Assert.Equal("1", loaded[1].Detail);
        Assert.True(loaded[1].Read);
    }

    [Fact]
    public void CorruptActivityFileIsIgnored()
    {
        using var env = new TempEnv();
        Directory.CreateDirectory(env.Paths.AppState);
        File.WriteAllText(env.Paths.NotificationsFile, "{ this is not json");

        Assert.Empty(new NotificationStore(env.Paths).Load());
    }
}
