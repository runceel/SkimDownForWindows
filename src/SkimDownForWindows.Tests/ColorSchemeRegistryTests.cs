using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Theme;
using SkimDownForWindows.Domain;
using SkimDownForWindows.Tests.TestHelpers;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class ColorSchemeRegistryTests
{
    private InMemoryColorSchemeSource _source = null!;
    private ColorSchemeRegistry _registry = null!;

    [TestInitialize]
    public void Setup()
    {
        _source = new InMemoryColorSchemeSource();
        _registry = new ColorSchemeRegistry(_source);
    }

    [TestMethod]
    public void Reload_DiscoversAndSortsByDisplayName()
    {
        _source.Add("zeta", """{"name":"Zulu","type":"dark","colors":{}}""");
        _source.Add("alpha", """{"name":"Alpha","type":"light","colors":{}}""");
        _registry.Reload();

        var names = _registry.Schemes.Select(s => s.DisplayName).ToList();
        CollectionAssert.AreEqual(new[] { "Alpha", "Zulu" }, names);
    }

    [TestMethod]
    public void Reload_SkipsInvalidJson()
    {
        _source.Add("good", """{"name":"Good","type":"dark","colors":{}}""");
        _source.Add("bad", "not json");
        _registry.Reload();

        Assert.AreEqual(1, _registry.Schemes.Count);
        Assert.AreEqual("good", _registry.Schemes[0].Id);
    }

    [TestMethod]
    public void Reload_RaisesThemesChanged()
    {
        var fired = 0;
        _registry.ThemesChanged += () => fired++;
        _registry.Reload();
        Assert.AreEqual(1, fired);

        _source.Add("a", """{"type":"dark","colors":{}}""");
        _registry.Reload();
        Assert.AreEqual(2, fired);
    }

    [TestMethod]
    public void Resolve_ReturnsNullForUnknownId()
    {
        _registry.Reload();
        Assert.IsNull(_registry.Resolve("missing"));
        Assert.IsNull(_registry.Resolve(null));
        Assert.IsNull(_registry.Resolve(string.Empty));
    }

    [TestMethod]
    public void Resolve_ReturnsCachedResolvedThemeForSameId()
    {
        _source.Add("monokai", """{"name":"Monokai","type":"dark","colors":{"editor.background":"#272822"}}""");
        _registry.Reload();

        var first = _registry.Resolve("monokai");
        var second = _registry.Resolve("monokai");
        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
        Assert.AreEqual("#272822", first.CssVariables["--skim-bg"]);
        Assert.IsTrue(first.IsDark);
    }

    [TestMethod]
    public void Reload_InvalidatesResolvedCacheOnContentChange()
    {
        _source.Add("monokai", """{"name":"Monokai","type":"dark","colors":{"editor.background":"#111111"}}""");
        _registry.Reload();
        var first = _registry.Resolve("monokai")!;
        Assert.AreEqual("#111111", first.CssVariables["--skim-bg"]);

        // Replace the JSON for the same id; Reload should drop the cached resolved.
        _source.Add("monokai", """{"name":"Monokai","type":"dark","colors":{"editor.background":"#222222"}}""");
        _registry.Reload();
        var second = _registry.Resolve("monokai")!;
        Assert.AreEqual("#222222", second.CssVariables["--skim-bg"]);
    }

    [TestMethod]
    public void Normalize_PreservesBuiltInThemes()
    {
        _registry.Reload();
        Assert.AreEqual(ThemeSelection.System, _registry.Normalize(ThemeSelection.System));
        Assert.AreEqual(ThemeSelection.Light, _registry.Normalize(ThemeSelection.Light));
        Assert.AreEqual(ThemeSelection.Dark, _registry.Normalize(ThemeSelection.Dark));
    }

    [TestMethod]
    public void Normalize_FallsBackToSystemWhenCustomIdMissing()
    {
        _registry.Reload();
        Assert.AreEqual(ThemeSelection.System, _registry.Normalize(new ThemeSelection(AppTheme.Custom, "missing")));
        Assert.AreEqual(ThemeSelection.System, _registry.Normalize(new ThemeSelection(AppTheme.Custom, null)));
        Assert.AreEqual(ThemeSelection.System, _registry.Normalize(new ThemeSelection(AppTheme.Custom, "")));
    }

    [TestMethod]
    public void Normalize_PreservesValidCustomTheme()
    {
        _source.Add("monokai", """{"name":"Monokai","type":"dark","colors":{}}""");
        _registry.Reload();
        var sel = new ThemeSelection(AppTheme.Custom, "monokai");
        Assert.AreEqual(sel, _registry.Normalize(sel));
    }

    [TestMethod]
    public void Reload_NormalizesViaThemeChangedRegardlessOfContent()
    {
        // InMemorySource は同じ id を 1 つだけ保持するので、Registry の重複除外コードは間接的に守られる。
        // ここでは Reload を 2 回呼んでも一貫した状態を返すことだけ確認する。
        _source.Add("a", """{"type":"light","colors":{}}""");
        _registry.Reload();
        _registry.Reload();
        Assert.AreEqual(1, _registry.Schemes.Count);
    }
}
