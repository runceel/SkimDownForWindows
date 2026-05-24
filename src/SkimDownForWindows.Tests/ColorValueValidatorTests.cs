using SkimDownForWindows.Application.Theme;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class ColorValueValidatorTests
{
    [DataTestMethod]
    [DataRow("#000")]
    [DataRow("#000a")]
    [DataRow("#abcdef")]
    [DataRow("#abcdef12")]
    [DataRow("rgb(0, 0, 0)")]
    [DataRow("rgba(255, 0, 0, 0.5)")]
    [DataRow("hsl(120, 100%, 50%)")]
    [DataRow("hsla(120deg, 100%, 50%, 0.7)")]
    [DataRow("transparent")]
    [DataRow("TRANSPARENT")]
    [DataRow("  #1e1e1e  ")]
    public void SafeValues_AreAccepted(string raw)
    {
        Assert.IsTrue(ColorValueValidator.IsSafe(raw), $"expected safe: {raw}");
    }

    [DataTestMethod]
    [DataRow("#zzzzzz")]
    [DataRow("#12345")]
    [DataRow("#1234567")]
    [DataRow("red")]
    [DataRow("var(--evil)")]
    [DataRow("calc(100% - 1px)")]
    [DataRow("url(https://example.com/x.png)")]
    [DataRow("rgb(0,0,0); body { background: red")]
    [DataRow("rgb(0,0,0)}")]
    [DataRow("rgb(0,0,0)<script>")]
    [DataRow("@import 'evil.css'")]
    [DataRow("")]
    [DataRow("   ")]
    public void UnsafeValues_AreRejected(string raw)
    {
        Assert.IsFalse(ColorValueValidator.IsSafe(raw), $"expected unsafe: {raw}");
    }

    [TestMethod]
    public void Normalize_TrimsAndPreservesOriginalCasing()
    {
        Assert.AreEqual("#FFFFFF", ColorValueValidator.Normalize("  #FFFFFF  "));
        Assert.AreEqual("rgb(0, 0, 0)", ColorValueValidator.Normalize("rgb(0, 0, 0)"));
    }

    [TestMethod]
    public void Normalize_RejectsValuesOverMaxLength()
    {
        var tooLong = "#" + new string('a', ColorValueValidator.MaxLength);
        Assert.IsNull(ColorValueValidator.Normalize(tooLong));
    }
}
