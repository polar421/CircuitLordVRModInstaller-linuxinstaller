using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CircuitLord.VRModInstaller.Tests.TestAppBuilder))]

namespace CircuitLord.VRModInstaller.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
