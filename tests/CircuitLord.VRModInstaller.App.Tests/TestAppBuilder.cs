using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CircuitLord.VRModInstaller.Tests.TestAppBuilder))]

namespace CircuitLord.VRModInstaller.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            // Screenshot checks need the real Skia rasterizer. The headless
            // drawing backend does not produce a frame to capture.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
