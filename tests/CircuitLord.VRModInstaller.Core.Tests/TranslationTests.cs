using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitLord.VRModInstaller.Core.Localization;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class TranslationTests
{
    [Fact]
    public void EverySupportedLanguageHasTheCompleteEnglishKeySet()
    {
        var english = TranslationCatalog.English;
        Assert.NotEmpty(english);
        Assert.Equal(10, TranslationCatalog.SupportedLanguages.Count);

        foreach (var language in TranslationCatalog.SupportedLanguages)
        {
            var table = TranslationCatalog.Table(language.Code);
            Assert.False(table.Count == 0, $"{language.Code} has no catalog");
            Assert.Equal(english.Count, table.Count);
            foreach (var key in english.Keys)
            {
                Assert.True(table.ContainsKey(key), $"{language.Code} is missing {key}");
                Assert.False(string.IsNullOrWhiteSpace(table[key]), $"{language.Code} has an empty {key}");
            }
        }
    }

    [Fact]
    public void PlaceholdersArePreservedInEveryTranslation()
    {
        var english = TranslationCatalog.English;
        foreach (var language in TranslationCatalog.SupportedLanguages)
        {
            var table = TranslationCatalog.Table(language.Code);
            foreach (var (key, source) in english)
            {
                var expected = Placeholders(source);
                var actual = Placeholders(table[key]);
                Assert.True(expected.SetEquals(actual),
                    $"{language.Code} changed placeholders in {key}: {source} -> {table[key]}");
            }
        }
    }

    [Fact]
    public void MissingKeysAndUnsupportedLocalesFallBackToEnglish()
    {
        try
        {
            TranslationCatalog.Use("fr");
            Assert.Equal("Choisissez un jeu", TranslationCatalog.Get("chooseGame"));
            Assert.Equal("__not_a_real_key__", TranslationCatalog.Get("__not_a_real_key__"));

            Assert.Equal("pt-BR", TranslationCatalog.Normalize("pt-br"));
            Assert.Equal("zh-Hans", TranslationCatalog.Normalize("ZH-HANS"));
            Assert.Equal("en", TranslationCatalog.Normalize("pt-PT"));
            Assert.Equal("en", TranslationCatalog.Normalize("zh-CN"));
            Assert.Equal("en", TranslationCatalog.Normalize("en-US"));

            TranslationCatalog.Use("pt-PT");
            Assert.Equal(TranslationCatalog.English["chooseGame"], TranslationCatalog.Get("chooseGame"));
        }
        finally
        {
            TranslationCatalog.Use("en");
        }
    }

    [Fact]
    public void EveryLanguageKeepsTechnicalIdentifiers()
    {
        string[] identifiers =
        [
            "WiVRn", "OpenXR", "Monado", "SteamVR", "OpenVR", "Proton",
            "CircuitLord", "Steam", "tf2vr",
        ];

        foreach (var language in TranslationCatalog.SupportedLanguages)
        {
            var table = TranslationCatalog.Table(language.Code);
            foreach (var (key, source) in TranslationCatalog.English)
            {
                foreach (var identifier in identifiers)
                {
                    if (source.Contains(identifier, StringComparison.Ordinal))
                    {
                        Assert.True(table[key].Contains(identifier, StringComparison.Ordinal),
                            $"{language.Code} lost {identifier} in {key}: {table[key]}");
                    }
                }
            }
        }
    }

    [Fact]
    public void TranslationCatalogHasNoUnusedKeys()
    {
        var repository = FindRepositoryRoot();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(repository, "src", "CircuitLord.VRModInstaller.App"),
                     "*.*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"Key=([A-Za-z0-9_]+)"))
            {
                used.Add(match.Groups[1].Value);
            }
            foreach (Match match in Regex.Matches(text, @"Loc\.(?:Get|Format)\(\s*""([A-Za-z0-9_]+)"""))
            {
                used.Add(match.Groups[1].Value);
            }
            foreach (Match match in Regex.Matches(text, @"SetStatusText\([^\n]*,\s*""([A-Za-z0-9_]+)""\s*,\s*""([A-Za-z0-9_]+)"""))
            {
                used.Add(match.Groups[1].Value);
                used.Add(match.Groups[2].Value);
            }
            foreach (Match match in Regex.Matches(text, @"SetStage\((?<args>[\s\S]*?)\);", RegexOptions.CultureInvariant))
            {
                foreach (Match key in Regex.Matches(match.Groups["args"].Value, @"""([A-Za-z0-9_]+)"""))
                {
                    used.Add(key.Groups[1].Value);
                }
            }
            foreach (Match match in Regex.Matches(text, @"(?:SetUpdateStatus|SetBackendUpdateStatus)\((?<args>[\s\S]*?)\);", RegexOptions.CultureInvariant))
            {
                foreach (Match key in Regex.Matches(match.Groups["args"].Value, @"""([A-Za-z0-9_]+)"""))
                {
                    used.Add(key.Groups[1].Value);
                }
            }
        }

        // These are selected dynamically by the changelog expand button.
        used.Add("showAll");
        used.Add("showLess");

        var unused = TranslationCatalog.English.Keys
            .Where(key => !used.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(unused);
    }

    [Fact]
    public void FormattingUsesTheCurrentLocale()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            TranslationCatalog.Use("en");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var formatted = TranslationCatalog.Format("catalogueSummary", "Katalog", 12345.5);
            Assert.Contains("12345,5", formatted, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            TranslationCatalog.Use("en");
        }
    }

    private static HashSet<string> Placeholders(string value)
        => Regex.Matches(value, @"\{\d+\}")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
