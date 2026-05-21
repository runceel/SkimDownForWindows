using System;
using System.IO;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class PathHelpersTests
{
    [TestMethod]
    public void IsMarkdownFile_RecognizesMdAndMarkdownCaseInsensitive()
    {
        Assert.IsTrue(PathHelpers.IsMarkdownFile("README.md"));
        Assert.IsTrue(PathHelpers.IsMarkdownFile("README.MD"));
        Assert.IsTrue(PathHelpers.IsMarkdownFile("notes.markdown"));
        Assert.IsTrue(PathHelpers.IsMarkdownFile("notes.Markdown"));
        Assert.IsFalse(PathHelpers.IsMarkdownFile("README.txt"));
        Assert.IsFalse(PathHelpers.IsMarkdownFile("img.mdx")); // .mdx is not in scope per SPEC
        Assert.IsFalse(PathHelpers.IsMarkdownFile(""));
    }

    [TestMethod]
    public void IsInsideFolder_AllowsSameFolderAndDescendants()
    {
        var root = Path.GetTempPath();
        var child = Path.Combine(root, "sub", "file.md");
        Assert.IsTrue(PathHelpers.IsInsideFolder(root, root));
        Assert.IsTrue(PathHelpers.IsInsideFolder(root, child));
    }

    [TestMethod]
    public void IsInsideFolder_RejectsSiblingsAndOutside()
    {
        // C:\Temp\foo  vs  C:\Temp\foobar — should NOT match (prefix-string trap).
        var foo = Path.Combine(Path.GetTempPath(), "foo");
        var foobar = Path.Combine(Path.GetTempPath(), "foobar", "x.md");
        Assert.IsFalse(PathHelpers.IsInsideFolder(foo, foobar));
    }

    [TestMethod]
    public void IsInsideFolder_NormalizesDotDotEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "skimtest_root");
        // Crafted path that resolves OUTSIDE the root via "..".
        var escape = Path.Combine(root, "sub", "..", "..", "elsewhere", "x.md");
        Assert.IsFalse(PathHelpers.IsInsideFolder(root, escape));
    }

    [TestMethod]
    public void RelativeFromRoot_UsesForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "skim");
        var file = Path.Combine(root, "a", "b", "c.md");
        var rel = PathHelpers.RelativeFromRoot(root, file);
        Assert.AreEqual("a/b/c.md", rel);
    }

    [TestMethod]
    public void RelativeFromRoot_RootItself_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "skim");
        Assert.AreEqual(string.Empty, PathHelpers.RelativeFromRoot(root, root));
    }

    [TestMethod]
    public void RelativeFromRoot_OutsideRoot_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "skim");
        var outside = Path.Combine(Path.GetTempPath(), "other", "x.md");
        Assert.ThrowsExactly<InvalidOperationException>(() => PathHelpers.RelativeFromRoot(root, outside));
    }
}
