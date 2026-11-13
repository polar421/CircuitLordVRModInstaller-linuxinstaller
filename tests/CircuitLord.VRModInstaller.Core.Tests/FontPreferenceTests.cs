using System;
using CircuitLord.VRModInstaller.Core.Appearance;

namespace CircuitLord.VRModInstaller.Core.Tests;

public class FontPreferenceTests
{
    [Fact]
    public void DefaultUiFontIsTheBundledLiberationSerif()
    {
        var resolution = FontPreference.ResolveUiFont(
            FontPreference.DefaultUiFontId,
            new[] { FontPreference.DefaultUiFontId });

        Assert.Equal(FontPreference.DefaultUiFontId, resolution.EffectiveFontId);
        Assert.False(resolution.IsUnavailable);
        Assert.Equal("Liberation Serif", FontPreference.DescribeFontId(resolution.EffectiveFontId));
    }

    [Fact]
    public void MissingUiFontFallsBackWithoutLosingTheSavedIdentifier()
    {
        const string missing = "system:A Font That Was Uninstalled";
        var resolution = FontPreference.ResolveUiFont(
            missing,
            new[] { FontPreference.DefaultUiFontId });

        Assert.Equal(FontPreference.DefaultUiFontId, resolution.EffectiveFontId);
        Assert.True(resolution.IsUnavailable);
        Assert.Equal(missing, resolution.SavedFontId);
        Assert.Equal("A Font That Was Uninstalled", FontPreference.DescribeFontId(resolution.SavedFontId));
    }

    [Fact]
    public void MissingTechnicalFontFallsBackToTheInterfaceFont()
    {
        const string missing = "system:A Monospace That Was Uninstalled";
        var resolution = FontPreference.ResolveTechnicalFont(
            missing,
            Array.Empty<string>());

        Assert.Equal(FontPreference.FollowInterfaceFontId, resolution.EffectiveFontId);
        Assert.True(resolution.IsUnavailable);
        Assert.Equal(missing, resolution.SavedFontId);
    }

    [Fact]
    public void TechnicalFontCanFollowTheInterfaceFont()
    {
        var resolution = FontPreference.ResolveTechnicalFont(
            FontPreference.FollowInterfaceFontId,
            Array.Empty<string>());

        Assert.Equal(FontPreference.FollowInterfaceFontId, resolution.EffectiveFontId);
        Assert.False(resolution.IsUnavailable);
        Assert.Null(resolution.SavedFontId);
    }

    [Fact]
    public void InstalledFontResolutionUsesTheCatalogCasing()
    {
        var resolution = FontPreference.ResolveUiFont(
            "system:dejavu sans mono",
            new[] { "system:DejaVu Sans Mono" });

        Assert.Equal("system:DejaVu Sans Mono", resolution.EffectiveFontId);
        Assert.False(resolution.IsUnavailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-font")]
    [InlineData("system:")]
    public void InvalidUiFontIdentifiersNormalizeToTheBundledDefault(string? saved)
    {
        Assert.Equal(FontPreference.DefaultUiFontId, FontPreference.NormalizeUiFont(saved));
    }

    [Fact]
    public void InvalidTechnicalFontIdentifiersNormalizeToFollowingTheInterface()
    {
        Assert.Equal(
            FontPreference.FollowInterfaceFontId,
            FontPreference.NormalizeTechnicalFont("not-a-font"));
    }
}
