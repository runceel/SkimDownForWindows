using System.Collections.Generic;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Theme;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class ResolvedThemeTests
{
    [TestMethod]
    public void Resolve_MapsCommonVSCodeKeysToCssVariables()
    {
        var scheme = new ColorScheme(
            id: "test",
            displayName: "Test",
            type: ColorSchemeType.Dark,
            colors: new Dictionary<string, string>
            {
                ["editor.background"] = "#1e1e1e",
                ["editor.foreground"] = "#d4d4d4",
                ["textLink.foreground"] = "#3794ff",
                ["panel.border"] = "#333333",
                ["editor.findMatchBackground"] = "#515c6a",
                ["editor.findMatchHighlightBackground"] = "#ea5c00",
            });

        var resolved = ResolvedTheme.Resolve(scheme);

        Assert.AreEqual("test", resolved.Id);
        Assert.AreEqual(ColorSchemeType.Dark, resolved.Type);
        Assert.IsTrue(resolved.IsDark);
        Assert.AreEqual("#1e1e1e", resolved.CssVariables["--skim-bg"]);
        Assert.AreEqual("#d4d4d4", resolved.CssVariables["--skim-fg"]);
        Assert.AreEqual("#3794ff", resolved.CssVariables["--skim-link"]);
        Assert.AreEqual("#333333", resolved.CssVariables["--skim-border"]);
        Assert.AreEqual("#515c6a", resolved.CssVariables["--skim-mark-current-bg"]);
        Assert.AreEqual("#ea5c00", resolved.CssVariables["--skim-mark-bg"]);
    }

    [TestMethod]
    public void Resolve_AppliesDarkFallbackForMissingKeys()
    {
        var scheme = new ColorScheme("t", "T", ColorSchemeType.Dark, new Dictionary<string, string>());
        var resolved = ResolvedTheme.Resolve(scheme);

        Assert.AreEqual(FallbackPalette.Dark["--skim-bg"], resolved.CssVariables["--skim-bg"]);
        Assert.AreEqual(FallbackPalette.Dark["--skim-fg"], resolved.CssVariables["--skim-fg"]);
        Assert.IsTrue(resolved.IsDark);
    }

    [TestMethod]
    public void Resolve_AppliesLightFallbackForMissingKeys()
    {
        var scheme = new ColorScheme("t", "T", ColorSchemeType.Light, new Dictionary<string, string>());
        var resolved = ResolvedTheme.Resolve(scheme);

        Assert.AreEqual(FallbackPalette.Light["--skim-bg"], resolved.CssVariables["--skim-bg"]);
        Assert.AreEqual(FallbackPalette.Light["--skim-fg"], resolved.CssVariables["--skim-fg"]);
        Assert.IsFalse(resolved.IsDark);
    }

    [TestMethod]
    public void Resolve_PrefersHigherPriorityKey()
    {
        var scheme = new ColorScheme("t", "T", ColorSchemeType.Light, new Dictionary<string, string>
        {
            ["panel.border"] = "#aaaaaa",
            ["editorGroup.border"] = "#bbbbbb",
            ["editorWidget.border"] = "#cccccc",
        });
        var resolved = ResolvedTheme.Resolve(scheme);

        Assert.AreEqual("#aaaaaa", resolved.CssVariables["--skim-border"]);
    }

    [TestMethod]
    public void Resolve_RejectsUnsafeColorValuesAndFallsBack()
    {
        var scheme = new ColorScheme("t", "T", ColorSchemeType.Light, new Dictionary<string, string>
        {
            ["editor.background"] = "#ffffff; } body { background: red",
            ["editor.foreground"] = "url(https://example.com/tracker)",
            ["textLink.foreground"] = "rgb(10, 20, 30)",
        });

        var resolved = ResolvedTheme.Resolve(scheme);

        // unsafe values fall back to light palette
        Assert.AreEqual(FallbackPalette.Light["--skim-bg"], resolved.CssVariables["--skim-bg"]);
        Assert.AreEqual(FallbackPalette.Light["--skim-fg"], resolved.CssVariables["--skim-fg"]);
        // safe rgb() passes through
        Assert.AreEqual("rgb(10, 20, 30)", resolved.CssVariables["--skim-link"]);
    }

    [TestMethod]
    public void Resolve_AcceptsHexAlphaColor()
    {
        var scheme = new ColorScheme("t", "T", ColorSchemeType.Dark, new Dictionary<string, string>
        {
            ["editor.findMatchBackground"] = "#515c6aaa",
        });
        var resolved = ResolvedTheme.Resolve(scheme);
        Assert.AreEqual("#515c6aaa", resolved.CssVariables["--skim-mark-current-bg"]);
    }
}
