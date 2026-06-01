using SkimDownForWindows.Application.Utilities;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Tests;

/// <summary>
/// <see cref="ContentMaxWidthMap"/> の値マッピングを固定化する単体テスト。
/// </summary>
[TestClass]
public sealed class ContentMaxWidthMapTests
{
    [TestMethod]
    public void ToCssValue_Standard_Is760px()
        => Assert.AreEqual("760px", ContentMaxWidthMap.ToCssValue(ContentMaxWidth.Standard));

    [TestMethod]
    public void ToCssValue_Wide_Is960px()
        => Assert.AreEqual("960px", ContentMaxWidthMap.ToCssValue(ContentMaxWidth.Wide));

    [TestMethod]
    public void ToCssValue_ExtraWide_Is1200px()
        => Assert.AreEqual("1200px", ContentMaxWidthMap.ToCssValue(ContentMaxWidth.ExtraWide));

    [TestMethod]
    public void ToCssValue_Full_IsNone()
        => Assert.AreEqual("none", ContentMaxWidthMap.ToCssValue(ContentMaxWidth.Full));

    [TestMethod]
    public void ToCssValue_UnknownValue_FallsBackToStandard()
    {
        // 範囲外の値は Standard 相当 (760px) に倒す。永続化されている enum 値が
        // 後方互換性の都合で未知になった場合の防御。
        Assert.AreEqual("760px", ContentMaxWidthMap.ToCssValue((ContentMaxWidth)999));
    }
}
