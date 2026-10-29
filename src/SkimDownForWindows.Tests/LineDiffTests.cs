using SkimDownForWindows.Application.Markdown;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class LineDiffTests
{
    [TestMethod]
    public void Lines_NormalizesBomAndLineEndings()
    {
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, LineDiff.Lines("\uFEFFone\r\ntwo\rthree\n"));
        Assert.HasCount(0, LineDiff.Lines(""));
        Assert.HasCount(0, LineDiff.Hunks("one\r\n", "one\n"));
    }

    [TestMethod]
    public void Hunks_GroupsReplacementsAndRetainsPreviousMarkdown()
    {
        var hunks = LineDiff.Hunks("same\nold\nsame\n", "same\nnew\nsame\nadded\n");
        Assert.HasCount(2, hunks);
        Assert.AreEqual(1, hunks[0].NewStart);
        Assert.AreEqual(1, hunks[0].NewCount);
        Assert.AreEqual(1, hunks[0].OldStart);
        CollectionAssert.AreEqual(new[] { "old" }, hunks[0].OldLines);
        Assert.AreEqual(3, hunks[1].NewStart);
        Assert.AreEqual(0, hunks[1].OldCount);
    }

    [TestMethod]
    public void Hunks_DeletionAtEndHasZeroNewCount()
    {
        var hunk = LineDiff.Hunks("keep\nremove\n", "keep\n").Single();
        Assert.AreEqual(1, hunk.NewStart);
        Assert.AreEqual(0, hunk.NewCount);
        CollectionAssert.AreEqual(new[] { "remove" }, hunk.OldLines);
    }

    [TestMethod]
    public void Hunks_LargeRewrite_PreservesCommonBoundaries()
    {
        var oldText = "prefix\n" + string.Join('\n', Enumerable.Range(0, 1100).Select(i => "old" + i)) + "\nsuffix\n";
        var newText = "prefix\n" + string.Join('\n', Enumerable.Range(0, 1100).Select(i => "new" + i)) + "\nsuffix\n";
        var hunk = LineDiff.Hunks(oldText, newText).Single();
        Assert.AreEqual(1, hunk.NewStart);
        Assert.AreEqual(1, hunk.OldStart);
        Assert.AreEqual(1100, hunk.NewCount);
        Assert.AreEqual(1100, hunk.OldCount);
    }

    [TestMethod]
    public void Hunks_RandomEdits_ReconstructOldDocument()
    {
        var random = new Random(7129);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var oldLines = Enumerable.Range(0, random.Next(30)).Select(_ => random.Next(8).ToString()).ToArray();
            var newLines = Enumerable.Range(0, random.Next(30)).Select(_ => random.Next(8).ToString()).ToArray();
            var hunks = LineDiff.Hunks(string.Join('\n', oldLines), string.Join('\n', newLines));
            var rebuilt = newLines.ToList();
            foreach (var hunk in hunks.Reverse())
            {
                rebuilt.RemoveRange(hunk.NewStart, hunk.NewCount);
                rebuilt.InsertRange(hunk.NewStart, hunk.OldLines);
            }
            CollectionAssert.AreEqual(oldLines, rebuilt.ToArray(), $"iteration {iteration}");
        }
    }
}
