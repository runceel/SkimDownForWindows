using System.IO;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class InitialSelectionPickerTests
{
    private static string TestRoot => Path.Combine(Path.GetTempPath(), "skim-pick");

    private static MarkdownTreeItem BuildSample()
    {
        var files = new[]
        {
            Path.Combine(TestRoot, "alpha", "deep.md"),
            Path.Combine(TestRoot, "README.md"),
            Path.Combine(TestRoot, "zeta.md"),
        };
        return new MarkdownTreeBuilder().Build(TestRoot, files);
    }

    [TestMethod]
    public void Pick_PrefersLastSelectedWhenStillPresent()
    {
        var root = BuildSample();
        var picked = new InitialSelectionPicker().Pick(root, "zeta.md");
        Assert.IsNotNull(picked);
        StringAssert.EndsWith(picked!, "zeta.md");
    }

    [TestMethod]
    public void Pick_FallsBackToReadmeWhenLastSelectedMissing()
    {
        var root = BuildSample();
        var picked = new InitialSelectionPicker().Pick(root, "missing.md");
        Assert.IsNotNull(picked);
        StringAssert.EndsWith(picked!, "README.md");
    }

    [TestMethod]
    public void Pick_FallsBackToFirstWhenNoReadme()
    {
        var files = new[]
        {
            Path.Combine(TestRoot, "alpha", "deep.md"),
            Path.Combine(TestRoot, "zeta.md"),
        };
        var root = new MarkdownTreeBuilder().Build(TestRoot, files);
        var picked = new InitialSelectionPicker().Pick(root, null);
        Assert.IsNotNull(picked);
        // Folder-first iteration -> "alpha/deep.md" comes before "zeta.md".
        StringAssert.EndsWith(picked!, "deep.md");
    }

    [TestMethod]
    public void Pick_EmptyTree_ReturnsNull()
    {
        var root = new MarkdownTreeBuilder().Build(TestRoot, System.Array.Empty<string>());
        var picked = new InitialSelectionPicker().Pick(root, null);
        Assert.IsNull(picked);
    }

    [TestMethod]
    public void Pick_NullTree_ReturnsNull()
    {
        var picked = new InitialSelectionPicker().Pick(null!, "anything");
        Assert.IsNull(picked);
    }

    [TestMethod]
    public void Pick_ReadmeMarkdownVariant_IsAcceptedAsFallback()
    {
        // SPEC: ".markdown" 拡張子 README もフォールバック対象。
        var files = new[]
        {
            Path.Combine(TestRoot, "alpha", "deep.md"),
            Path.Combine(TestRoot, "README.markdown"),
            Path.Combine(TestRoot, "zeta.md"),
        };
        var root = new MarkdownTreeBuilder().Build(TestRoot, files);
        var picked = new InitialSelectionPicker().Pick(root, null);
        Assert.IsNotNull(picked);
        StringAssert.EndsWith(picked!, "README.markdown");
    }

    [TestMethod]
    public void Pick_ReadmeCaseVariant_IsAcceptedAsFallback()
    {
        // 大文字小文字を区別しない: "readme.md" でもフォールバック対象。
        var files = new[]
        {
            Path.Combine(TestRoot, "alpha", "deep.md"),
            Path.Combine(TestRoot, "readme.MD"),
            Path.Combine(TestRoot, "zeta.md"),
        };
        var root = new MarkdownTreeBuilder().Build(TestRoot, files);
        var picked = new InitialSelectionPicker().Pick(root, null);
        Assert.IsNotNull(picked);
        StringAssert.EndsWith(picked!, "readme.MD");
    }
}
