using System;
using System.Linq;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Tests.TestHelpers;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class RecentMarkdownListBuilderTests
{
    private const string Root = @"C:\skimroot";

    private static DateTimeOffset Utc(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Build_OrdersByLastModified_Descending_AsFlatLeaves()
    {
        var fs = new FakeFileSystem();
        var old = @"C:\skimroot\old.md";
        var mid = @"C:\skimroot\docs\mid.md";
        var recent = @"C:\skimroot\recent.md";
        fs.AddFile(old, Utc(2020, 1, 1));
        fs.AddFile(mid, Utc(2022, 6, 1));
        fs.AddFile(recent, Utc(2024, 12, 31));

        var root = new RecentMarkdownListBuilder(fs).Build(Root, new[] { old, mid, recent });

        CollectionAssert.AreEqual(
            new[] { "recent.md", "mid.md", "old.md" },
            root.Children.Select(c => c.Name).ToArray());
        Assert.AreEqual(3, root.MarkdownCount);
        Assert.IsTrue(root.Children.All(c => !c.IsFolder), "All items should be flat leaves.");
    }

    [TestMethod]
    public void Build_SameTimestamp_TieBreaksByNameThenRelativePath()
    {
        var fs = new FakeFileSystem();
        var ts = Utc(2023, 5, 5);
        var bReadme = @"C:\skimroot\b\readme.md";
        var aReadme = @"C:\skimroot\a\readme.md";
        var apple = @"C:\skimroot\apple.md";
        fs.AddFile(bReadme, ts);
        fs.AddFile(aReadme, ts);
        fs.AddFile(apple, ts);

        var root = new RecentMarkdownListBuilder(fs).Build(Root, new[] { bReadme, aReadme, apple });

        // Name asc (apple before readme); the two readme.md tie-break by RelativePath asc (a before b).
        CollectionAssert.AreEqual(
            new[] { "apple.md", "a/readme.md", "b/readme.md" },
            root.Children.Select(c => c.RelativePath).ToArray());
    }

    [TestMethod]
    public void Build_SetsRelativeFolderAndLastModified()
    {
        var fs = new FakeFileSystem();
        var nested = @"C:\skimroot\docs\sub\a.md";
        var rootFile = @"C:\skimroot\r.md";
        fs.AddFile(nested, Utc(2024, 1, 2));
        fs.AddFile(rootFile, Utc(2024, 1, 1));

        var root = new RecentMarkdownListBuilder(fs).Build(Root, new[] { nested, rootFile });

        var nestedItem = root.Children.Single(c => c.Name == "a.md");
        var rootItem = root.Children.Single(c => c.Name == "r.md");

        Assert.AreEqual("docs/sub", nestedItem.RelativeFolder);
        Assert.AreEqual(string.Empty, rootItem.RelativeFolder);
        Assert.AreEqual(Utc(2024, 1, 2), nestedItem.LastModified);
    }

    [TestMethod]
    public void Build_SkipsPathsOutsideRoot()
    {
        var fs = new FakeFileSystem();
        var inside = @"C:\skimroot\a.md";
        var outside = @"C:\other\x.md";
        fs.AddFile(inside, Utc(2024, 1, 1));
        fs.AddFile(outside, Utc(2024, 1, 2));

        var root = new RecentMarkdownListBuilder(fs).Build(Root, new[] { inside, outside });

        CollectionAssert.AreEqual(new[] { "a.md" }, root.Children.Select(c => c.Name).ToArray());
    }

    [TestMethod]
    public void Build_UnknownTimestamp_SortsToBottom()
    {
        var fs = new FakeFileSystem();
        var known = @"C:\skimroot\known.md";
        var unknown = @"C:\skimroot\unknown.md";
        fs.AddFile(known, Utc(2021, 1, 1));
        // 'unknown' は AddFile しないため GetLastWriteTimeUtc は MinValue を返す。

        var root = new RecentMarkdownListBuilder(fs).Build(Root, new[] { unknown, known });

        CollectionAssert.AreEqual(
            new[] { "known.md", "unknown.md" },
            root.Children.Select(c => c.Name).ToArray());
    }
}
