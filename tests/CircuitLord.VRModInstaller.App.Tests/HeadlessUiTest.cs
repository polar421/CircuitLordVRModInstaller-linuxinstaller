using System;
using System.Threading;
using Avalonia.Headless;

namespace CircuitLord.VRModInstaller.Tests;

public abstract class HeadlessUiTest : IAsyncLifetime
{
    private static HeadlessUnitTestSession? _session;

    protected TemporaryHome Home { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Home = new TemporaryHome();
        // Loc and AppearanceManager are process-wide services with event handlers
        // retained by controls. Reusing one UI thread keeps every re-localization
        // on the dispatcher that owns those controls.
        _session ??= HeadlessUnitTestSession.StartNew(
            typeof(TestAppBuilder),
            AvaloniaTestIsolationLevel.PerAssembly);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Home.Dispose();
        return Task.CompletedTask;
    }

    protected Task Dispatch(Action action) =>
        _session!.Dispatch(action, CancellationToken.None);
}
