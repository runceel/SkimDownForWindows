using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class ColorSchemeTests
{
    [TestMethod]
    public void LoadFromJson_ParsesNameTypeAndColors()
    {
        var json = """
            {
              "name": "My Theme",
              "type": "dark",
              "colors": {
                "editor.background": "#1e1e1e",
                "editor.foreground": "#d4d4d4",
                "textLink.foreground": "#3794ff",
                "ignored": 42,
                "ignored2": null,
                "ignored3": {}
              }
            }
            """;
        var scheme = ColorScheme.LoadFromJson(json, "my-theme");

        Assert.IsNotNull(scheme);
        Assert.AreEqual("my-theme", scheme.Id);
        Assert.AreEqual("My Theme", scheme.DisplayName);
        Assert.AreEqual(ColorSchemeType.Dark, scheme.Type);
        Assert.AreEqual("#1e1e1e", scheme.Colors["editor.background"]);
        Assert.AreEqual("#d4d4d4", scheme.Colors["editor.foreground"]);
        Assert.AreEqual("#3794ff", scheme.Colors["textLink.foreground"]);
        Assert.IsFalse(scheme.Colors.ContainsKey("ignored"));
        Assert.IsFalse(scheme.Colors.ContainsKey("ignored2"));
        Assert.IsFalse(scheme.Colors.ContainsKey("ignored3"));
    }

    [TestMethod]
    public void LoadFromJson_FallsBackToIdWhenNameMissing()
    {
        var scheme = ColorScheme.LoadFromJson("""{"type":"light","colors":{}}""", "anonymous");
        Assert.IsNotNull(scheme);
        Assert.AreEqual("anonymous", scheme.Id);
        Assert.AreEqual("anonymous", scheme.DisplayName);
        Assert.AreEqual(ColorSchemeType.Light, scheme.Type);
    }

    [TestMethod]
    public void LoadFromJson_ReturnsNullForInvalidJson()
    {
        Assert.IsNull(ColorScheme.LoadFromJson("{ not valid", "broken"));
        Assert.IsNull(ColorScheme.LoadFromJson("123", "number"));
        Assert.IsNull(ColorScheme.LoadFromJson("\"just-a-string\"", "string"));
        Assert.IsNull(ColorScheme.LoadFromJson("{}", string.Empty)); // empty id
    }

    [TestMethod]
    public void LoadFromJson_DefaultsToDarkWhenTypeMissing()
    {
        var scheme = ColorScheme.LoadFromJson("""{"colors":{}}""", "notype");
        Assert.IsNotNull(scheme);
        Assert.AreEqual(ColorSchemeType.Dark, scheme.Type);
    }

    [TestMethod]
    public void LoadFromJson_RecognizesHighContrastTypes()
    {
        var hcLight = ColorScheme.LoadFromJson("""{"type":"hc-light","colors":{}}""", "hc-l");
        var hcBlack = ColorScheme.LoadFromJson("""{"type":"hc-black","colors":{}}""", "hc-b");

        Assert.AreEqual(ColorSchemeType.HighContrastLight, hcLight!.Type);
        Assert.AreEqual(ColorSchemeType.HighContrastDark, hcBlack!.Type);
        Assert.IsFalse(hcLight.Type.IsDark());
        Assert.IsTrue(hcBlack.Type.IsDark());
    }
}
