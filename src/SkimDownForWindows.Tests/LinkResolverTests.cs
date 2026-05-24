using System.IO;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Domain;

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

    [TestMethod]
    public void HashOnly_IsAnchor_WithEmptyAnchorString()
    {
        // 現コード: href が "#" 始まりなら Anchor。空アンカーは空文字として残る。
        var c = _r.Classify(Root, Origin, "#");
        Assert.AreEqual(LinkKind.Anchor, c.Kind);
        Assert.AreEqual(string.Empty, c.Anchor);
    }

    [TestMethod]
    public void FileScheme_InsideFolder_Markdown_IsClassified()
    {
        var localPath = Path.Combine(Root, "docs", "spec.md").Replace('\\', '/');
        var fileUri = new Uri("file:///" + localPath.TrimStart('/'));

        var c = _r.Classify(Root, Origin, fileUri.ToString());

        Assert.AreEqual(LinkKind.RelativeMarkdown, c.Kind);
        StringAssert.EndsWith(c.ResolvedFullPath!, "spec.md");
    }

    [TestMethod]
    public void FileScheme_OutsideFolder_IsOutOfFolder()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "x.md").Replace('\\', '/');
        var fileUri = new Uri("file:///" + outside.TrimStart('/'));

        var c = _r.Classify(Root, Origin, fileUri.ToString());

        Assert.AreEqual(LinkKind.OutOfFolder, c.Kind);
    }

    /// <summary>
    /// 現挙動の golden test: <c>file://...</c> URI の fragment は <see cref="Uri.LocalPath"/>
    /// に含まれず、anchor として伝播しない。テスト名で「現挙動」を明示する。
    /// </summary>
    [TestMethod]
    public void FileScheme_MarkdownWithFragment_DropsAnchor_CurrentBehavior()
    {
        var localPath = Path.Combine(Root, "docs", "spec.md").Replace('\\', '/');
        var fileUri = new Uri("file:///" + localPath.TrimStart('/') + "#section");

        var c = _r.Classify(Root, Origin, fileUri.ToString());

        Assert.AreEqual(LinkKind.RelativeMarkdown, c.Kind);
        Assert.IsNull(c.Anchor, "現挙動: file:// scheme では fragment が落ちる。");
    }

    [TestMethod]
    public void RelativeNonMarkdown_DoesNotCarryAnchor()
    {
        // パス側がアンカー付きでも、Markdown 以外なら ResolvedFullPath だけ返り Anchor は null。
        var c = _r.Classify(Root, Origin, "../img/cover.png#top");
        Assert.AreEqual(LinkKind.RelativeNonMarkdown, c.Kind);
        Assert.IsNull(c.Anchor);
    }

    [TestMethod]
    public void Whitespace_IsBlocked()
    {
        Assert.AreEqual(LinkKind.Blocked, _r.Classify(Root, Origin, "   ").Kind);
    }

    [TestMethod]
    public void UnknownScheme_IsBlocked()
    {
        Assert.AreEqual(LinkKind.Blocked, _r.Classify(Root, Origin, "ftp://example.com/x").Kind);
    }
}
