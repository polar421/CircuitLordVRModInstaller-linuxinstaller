using System.Reflection;
using System.Text.Json;

namespace CircuitLord.VRModInstaller.Core.Localization;

public sealed record LanguageDefinition(string Code, string NativeName, string EnglishName);

/// <summary>
/// Central translation catalog shared by the UI and the tests. Every supported
/// language is a complete table; missing keys fall back to English rather than
/// exposing a raw resource key or a blank label.
/// </summary>
public static class TranslationCatalog
{
    public const string FallbackLanguage = "en";

    public static IReadOnlyList<LanguageDefinition> SupportedLanguages { get; } =
    [
        new("en", "English", "English"),
        new("de", "Deutsch", "German"),
        new("es", "Español", "Spanish"),
        new("fr", "Français", "French"),
        new("pt-BR", "Português (Brasil)", "Brazilian Portuguese"),
        new("it", "Italiano", "Italian"),
        new("pl", "Polski", "Polish"),
        new("ru", "Русский", "Russian"),
        new("ja", "日本語", "Japanese"),
        new("zh-Hans", "简体中文", "Simplified Chinese"),
    ];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables =
        new(StringComparer.OrdinalIgnoreCase);

    static TranslationCatalog()
    {
        var assembly = typeof(TranslationCatalog).Assembly;
        foreach (var language in SupportedLanguages)
        {
            var suffix = $".Localization.Translations.{language.Code}.json";
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resource is null)
            {
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (table is not null)
                {
                    Tables[language.Code] = table;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A damaged table falls back to English for that language.
            }
        }

        CurrentLanguage = Normalize(FallbackLanguage);
    }

    public static string CurrentLanguage { get; private set; } = FallbackLanguage;

    public static IReadOnlyDictionary<string, string> English =>
        Tables.TryGetValue(FallbackLanguage, out var table)
            ? table
            : new Dictionary<string, string>();

    public static IReadOnlyList<string> Keys => English.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static IReadOnlyDictionary<string, string> Table(string language)
        => Tables.TryGetValue(language, out var table)
            ? table
            : new Dictionary<string, string>();

    public static LanguageDefinition? Find(string? language)
    {
        var normalized = Normalize(language);
        return SupportedLanguages.FirstOrDefault(entry => entry.Code == normalized);
    }

    /// <summary>
    /// Accepts only the locale identifiers the application actually ships.
    /// Unsupported variants intentionally fall back to English instead of
    /// being treated as an equivalent locale.
    /// </summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return FallbackLanguage;
        }

        var trimmed = language.Trim();
        foreach (var entry in SupportedLanguages)
        {
            if (string.Equals(entry.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Code;
            }
        }

        return FallbackLanguage;
    }

    public static bool IsSupported(string? language)
        => SupportedLanguages.Any(entry =>
            string.Equals(entry.Code, language?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static void Use(string? language)
    {
        CurrentLanguage = Normalize(language);
    }

    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (Tables.TryGetValue(CurrentLanguage, out var current)
            && current.TryGetValue(key, out var translated)
            && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }

        return English.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string Format(string key, params object?[] arguments)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), arguments);
}
