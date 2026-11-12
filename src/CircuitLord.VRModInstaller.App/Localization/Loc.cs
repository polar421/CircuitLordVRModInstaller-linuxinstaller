using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using CircuitLord.VRModInstaller.Core.Localization;

namespace CircuitLord.VRModInstaller.Localization;

/// <summary>
/// Application facade for the embedded translation catalog. The indexer is
/// observable, so <c>{loc:Tr}</c> bindings update immediately when the
/// language changes without reloading a view or restarting.
/// </summary>
public static class Loc
{
    private static readonly LocalizationNotifier Notifier = new();

    public static string Language => TranslationCatalog.CurrentLanguage;

    public static IReadOnlyList<LanguageDefinition> SupportedLanguages => TranslationCatalog.SupportedLanguages;

    public static LocalizationNotifier BindingSource => Notifier;

    public static event EventHandler? LanguageChanged;

    public static void Use(string? language)
    {
        var normalized = TranslationCatalog.Normalize(language);
        TranslationCatalog.Use(normalized);
        ApplyCulture(normalized);
        ApplyFont(normalized);
        Notifier.RaiseChanged();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key) => TranslationCatalog.Get(key);

    public static string Format(string key, params object?[] args)
        => TranslationCatalog.Format(key, args);

    public static string NormalizeLanguage(string? language) => TranslationCatalog.Normalize(language);

    public static bool IsSupported(string? language) => TranslationCatalog.IsSupported(language);

    private static void ApplyFont(string language)
    {
        if (Application.Current is null)
        {
            return;
        }

        var family = language switch
        {
            "ja" => "avares://CircuitLordVRModInstaller/Assets/Fonts#Noto Sans CJK JP",
            "zh-Hans" => "avares://CircuitLordVRModInstaller/Assets/Fonts#Noto Sans CJK SC",
            _ => "avares://CircuitLordVRModInstaller/Assets/Fonts#Liberation Serif",
        };
        Application.Current.Resources["UiFont"] = new FontFamily(family);
    }

    private static void ApplyCulture(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // The catalog still falls back to English; formatting stays usable.
        }
    }
}

public sealed class LocalizationNotifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.Get(key);

    internal void RaiseChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>XAML helper: <c>Text="{loc:Tr Key=chooseGame}"</c>.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Loc.BindingSource,
            Mode = BindingMode.OneWay,
        };
}
