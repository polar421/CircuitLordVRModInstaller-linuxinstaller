using Avalonia;

namespace CircuitLord.VRModInstaller;

internal static class Program
{
    [System.STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Native Wayland when a compositor is reachable; X11 otherwise.
            .UseWaylandWithFallback()
            // Must run after platform services exist and before App.axaml parses
            // cursor values, so Avalonia uses the desktop's Xcursor theme.
            .AfterPlatformServicesSetup(_ => LinuxSystemCursors.Install())
            .WithInterFont()
            .LogToTrace();
}
