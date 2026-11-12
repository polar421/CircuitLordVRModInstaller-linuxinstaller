using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CircuitLord.VRModInstaller.Core;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new SettingsStore(new XdgPaths()).Load();
            Loc.Use(settings.Language);
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
