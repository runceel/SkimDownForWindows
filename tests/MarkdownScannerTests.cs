using System.IO;
using System.Linq;
using SkimDownForWindows.Markdown;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class MarkdownScannerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "skim-scanner-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Teardown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Touch(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [TestMethod]
    public void Scan_DiscoversMdAndMarkdownRecursively()
    {
        Touch("README.md");
        Touch("notes.markdown");
        Touch("docs/intro.md");
        Touch("docs/deep/leaf.MD");

        var scanner = new MarkdownScanner();
        var results = scanner.Scan(_root).OrderBy(p => p).ToList();

        Assert.AreEqual(4, results.Count);
        CollectionAssert.AllItemsAreUnique(results);
    }

    [TestMethod]
    public void Scan_ExcludesGitNodeModulesBuildDerivedData()
    {
        Touch("README.md");
        Touch(".git/HEAD.md");
        Touch("node_modules/lib/x.md");
        Touch(".build/cache/y.md");
        Touch("DerivedData/inner/z.md");

        var scanner = new MarkdownScanner();
        var results = scanner.Scan(_root);

        Assert.AreEqual(1, results.Count);
        StringAssert.EndsWith(results[0], "README.md");
    }

    [TestMethod]
    public void Scan_ExcludesHiddenFilesAndDotPrefixedFolders()
    {
        Touch("real.md");
        Touch(".dotfile.md");
        Touch(".dotdir/x.md");

        var scanner = new MarkdownScanner();
        var results = scanner.Scan(_root);

        Assert.AreEqual(1, results.Count);
        StringAssert.EndsWith(results[0], "real.md");
    }

    [TestMethod]
    public void Scan_NonExistentFolder_ReturnsEmpty()
    {
        var scanner = new MarkdownScanner();
        var results = scanner.Scan(Path.Combine(_root, "nope"));
        Assert.AreEqual(0, results.Count);
    }
}
