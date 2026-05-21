using System.IO;
using SkimDownForWindows.Markdown;
using SkimDownForWindows.Models;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class LinkResolverTests
{
    private readonly LinkResolver _r = new();
    private static string Root => Path.Combine(Path.GetTempPath(), "skim-link-root");
    private static string Origin => Path.Combine(Root, "docs", "intro.md");

    [TestMethod]
    public void PureAnchor_IsAnchor()
    {
        var c = _r.Classify(Root, Origin, "#section");
        Assert.AreEqual(LinkKind.Anchor, c.Kind);
        Assert.AreEqual("section", c.Anchor);
    }

    [TestMethod]
    public void Http_IsExternal()
    {
        var c = _r.Classify(Root, Origin, "https://example.com/page");
        Assert.AreEqual(LinkKind.External, c.Kind);
        StringAssert.StartsWith(c.AbsoluteUri!, "https://");
    }

    [TestMethod]
    public void RelativeToSibling_Markdown_IsRelativeMarkdown()
    {
        var c = _r.Classify(Root, Origin, "../README.md");
        Assert.AreEqual(LinkKind.RelativeMarkdown, c.Kind);
        StringAssert.EndsWith(c.ResolvedFullPath!, "README.md");
    }

    [TestMethod]
    public void RelativeWithAnchor_KeepsAnchor()
    {
        var c = _r.Classify(Root, Origin, "../README.md#bottom");
        Assert.AreEqual(LinkKind.RelativeMarkdown, c.Kind);
        Assert.AreEqual("bottom", c.Anchor);
    }

    [TestMethod]
    public void RelativeNonMarkdown_IsClassifiedAsSuch()
    {
        var c = _r.Classify(Root, Origin, "../img/cover.png");
        Assert.AreEqual(LinkKind.RelativeNonMarkdown, c.Kind);
    }

    [TestMethod]
    public void OutOfFolder_IsBlocked()
    {
        var c = _r.Classify(Root, Origin, "../../escape.md");
        Assert.AreEqual(LinkKind.OutOfFolder, c.Kind);
    }

    [TestMethod]
    public void JavascriptScheme_IsBlocked()
    {
        var c = _r.Classify(Root, Origin, "javascript:alert(1)");
        Assert.AreEqual(LinkKind.Blocked, c.Kind);
    }

    [TestMethod]
    public void MailtoScheme_IsBlocked()
    {
        var c = _r.Classify(Root, Origin, "mailto:foo@example.com");
        Assert.AreEqual(LinkKind.Blocked, c.Kind);
    }

    [TestMethod]
    public void Empty_IsBlocked()
    {
        var c = _r.Classify(Root, Origin, "");
        Assert.AreEqual(LinkKind.Blocked, c.Kind);
    }

    [TestMethod]
    public void UrlEncodedRelative_DecodedThenResolved()
    {
        // "%20" decodes to a space; classify should still find the .md.
        var c = _r.Classify(Root, Origin, "../my%20notes.md");
        Assert.AreEqual(LinkKind.RelativeMarkdown, c.Kind);
        StringAssert.EndsWith(c.ResolvedFullPath!, "my notes.md");
    }
}
