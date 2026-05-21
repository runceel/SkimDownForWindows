using System.IO;
using System.Linq;
using SkimDownForWindows.Markdown;
using SkimDownForWindows.Models;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class MarkdownTreeBuilderTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "skim-tree-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Teardown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Path1(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    [TestMethod]
    public void Build_PutsFoldersBeforeFiles_CaseInsensitiveAlpha()
    {
        var files = new[]
        {
            Path1("zeta.md"),
            Path1("alpha.md"),
            Path1("docs", "a.md"),
            Path1("Apps", "a.md"),
            Path1("beta", "x.md"),
        };

        var root = new MarkdownTreeBuilder().Build(_root, files);
        var names = root.Children.Select(c => c.Name).ToArray();

        // Folders first (case-insensitive alpha), then files (alpha).
        CollectionAssert.AreEqual(new[] { "Apps", "beta", "docs", "alpha.md", "zeta.md" }, names);
    }

    [TestMethod]
    public void Build_OmitsFoldersWithoutAnyMarkdown()
    {
        // Tree builder ignores entries that aren't in the file list,
        // so simply not passing files under "empty" is enough.
        var files = new[]
        {
            Path1("README.md"),
            Path1("docs", "intro.md"),
        };
        var root = new MarkdownTreeBuilder().Build(_root, files);
        var names = root.Children.Select(c => c.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "docs", "README.md" }, names);
    }

    [TestMethod]
    public void Build_CountsMarkdownFilesOnRootAndFolders()
    {
        var files = new[]
        {
            Path1("README.md"),
            Path1("docs", "a.md"),
            Path1("docs", "b.md"),
            Path1("docs", "deep", "c.md"),
        };
        var root = new MarkdownTreeBuilder().Build(_root, files);
        Assert.AreEqual(4, root.MarkdownCount);

        var docs = root.Children.Single(c => c.Name == "docs");
        Assert.IsTrue(docs.IsFolder);
        Assert.AreEqual(3, docs.MarkdownCount);
    }

    [TestMethod]
    public void Build_SkipsFilesOutsideRoot()
    {
        var stranger = Path.Combine(Path.GetTempPath(), "stranger", "x.md");
        var files = new[] { Path1("README.md"), stranger };
        var root = new MarkdownTreeBuilder().Build(_root, files);
        Assert.AreEqual(1, root.Children.Count);
        Assert.AreEqual("README.md", root.Children[0].Name);
    }

    [TestMethod]
    public void Build_RelativePathUsesForwardSlashes()
    {
        var files = new[] { Path1("docs", "deep", "c.md") };
        var root = new MarkdownTreeBuilder().Build(_root, files);
        var docs = root.Children.Single();
        var deep = docs.Children.Single();
        var c = deep.Children.Single();
        Assert.AreEqual("docs", docs.RelativePath);
        Assert.AreEqual("docs/deep", deep.RelativePath);
        Assert.AreEqual("docs/deep/c.md", c.RelativePath);
    }
}
