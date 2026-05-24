using System.Text.Json;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class AppThemeJsonConverterTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new AppThemeJsonConverter());
        return options;
    }

    [TestMethod]
    public void Read_AcceptsLowercaseStrings()
    {
        var options = Options();
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("\"system\"", options));
        Assert.AreEqual(AppTheme.Light, JsonSerializer.Deserialize<AppTheme>("\"light\"", options));
        Assert.AreEqual(AppTheme.Dark, JsonSerializer.Deserialize<AppTheme>("\"dark\"", options));
        Assert.AreEqual(AppTheme.Custom, JsonSerializer.Deserialize<AppTheme>("\"custom\"", options));
    }

    [TestMethod]
    public void Read_AcceptsMixedCaseStrings()
    {
        var options = Options();
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("\"System\"", options));
        Assert.AreEqual(AppTheme.Light, JsonSerializer.Deserialize<AppTheme>("\"Light\"", options));
        Assert.AreEqual(AppTheme.Dark, JsonSerializer.Deserialize<AppTheme>("\"DARK\"", options));
    }

    [TestMethod]
    public void Read_AcceptsLegacyIntegerEncoding()
    {
        var options = Options();
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("0", options));
        Assert.AreEqual(AppTheme.Light, JsonSerializer.Deserialize<AppTheme>("1", options));
        Assert.AreEqual(AppTheme.Dark, JsonSerializer.Deserialize<AppTheme>("2", options));
        Assert.AreEqual(AppTheme.Custom, JsonSerializer.Deserialize<AppTheme>("3", options));
    }

    [TestMethod]
    public void Read_FallsBackToSystemForUnknown()
    {
        var options = Options();
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("\"garbage\"", options));
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("99", options));
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("null", options));
        Assert.AreEqual(AppTheme.System, JsonSerializer.Deserialize<AppTheme>("\"\"", options));
    }

    [TestMethod]
    public void Write_EmitsLowercaseStrings()
    {
        var options = Options();
        Assert.AreEqual("\"system\"", JsonSerializer.Serialize(AppTheme.System, options));
        Assert.AreEqual("\"light\"", JsonSerializer.Serialize(AppTheme.Light, options));
        Assert.AreEqual("\"dark\"", JsonSerializer.Serialize(AppTheme.Dark, options));
        Assert.AreEqual("\"custom\"", JsonSerializer.Serialize(AppTheme.Custom, options));
    }

    [TestMethod]
    public void Write_RoundTripsViaAppSettings()
    {
        var options = Options();
        var settings = new AppSettings { Theme = AppTheme.Custom, CustomThemeId = "monokai" };
        var json = JsonSerializer.Serialize(settings, options);
        Assert.IsTrue(json.Contains("\"Theme\": \"custom\"") || json.Contains("\"Theme\":\"custom\""));
        Assert.IsTrue(json.Contains("monokai"));

        var round = JsonSerializer.Deserialize<AppSettings>(json, options);
        Assert.IsNotNull(round);
        Assert.AreEqual(AppTheme.Custom, round.Theme);
        Assert.AreEqual("monokai", round.CustomThemeId);
    }

    [TestMethod]
    public void Read_LegacyIntegerSettingsJsonStillLoads()
    {
        var options = Options();
        // 旧フォーマット: Theme は整数として保存されていた settings.json を想定。
        var legacyJson = """{ "Theme": 2 }""";
        var round = JsonSerializer.Deserialize<AppSettings>(legacyJson, options);
        Assert.IsNotNull(round);
        Assert.AreEqual(AppTheme.Dark, round.Theme);
    }
}
