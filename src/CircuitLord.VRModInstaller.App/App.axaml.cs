using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CircuitLord.VRModInstaller.Appearance;
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
            var store = new SettingsStore(new XdgPaths());
            var settings = store.Load();
            var language = Loc.NormalizeLanguage(settings.Language);
            if (!string.Equals(settings.Language, language, System.StringComparison.Ordinal))
            {
                settings.Language = language;
                try
                {
                    store.Save(settings);
                }
                catch (System.IO.IOException)
                {
                    // A read-only configuration still uses the normalized language.
                }
            }

            Loc.Use(language);
            AppearanceManager.Initialize(settings);
            desktop.MainWindow = new MainWindow();
            AppearanceManager.ApplyMotionClasses(desktop.MainWindow);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
