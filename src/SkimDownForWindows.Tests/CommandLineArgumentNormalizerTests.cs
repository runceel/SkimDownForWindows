using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkimDownForWindows.Application.CommandLine;

namespace SkimDownForWindows.Tests;

[TestClass]
public class CommandLineArgumentNormalizerTests
{
    private const string Cwd = "C:\\work\\project";

    private static Func<string, bool> Exists(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return path => set.Contains(path);
    }

    [TestMethod]
    public void ToAbsolutePaths_NullOrEmpty_ReturnsEmpty()
    {
        Assert.IsEmpty(CommandLineArgumentNormalizer.ToAbsolutePaths(null, Cwd, _ => true));
        Assert.IsEmpty(CommandLineArgumentNormalizer.ToAbsolutePaths(Array.Empty<string>(), Cwd, _ => true));
    }

    [TestMethod]
    public void ToAbsolutePaths_CurrentDirectoryDot_BecomesAbsolute()
    {
        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { "." }, Cwd, Exists(Cwd));

        CollectionAssert.AreEqual(new[] { Cwd }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_RelativeFile_BecomesAbsolute()
    {
        var expected = Path.Combine(Cwd, "README.md");

        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { "README.md" }, Cwd, Exists(expected));

        CollectionAssert.AreEqual(new[] { expected }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_RelativeParentPath_IsResolved()
    {
        var expected = "C:\\work\\other";

        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { "..\\other" }, Cwd, Exists(expected));

        CollectionAssert.AreEqual(new[] { expected }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_AbsolutePath_IsUnchanged()
    {
        var absolute = "C:\\docs\\a.md";

        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { absolute }, Cwd, Exists(absolute));

        CollectionAssert.AreEqual(new[] { absolute }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_AbsolutePath_IsNotResolvedAgainstCurrentDirectory()
    {
        // 絶対パスは cwd の影響を受けない (別ドライブでも同じ)
        var absolute = "D:\\other\\b.md";

        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { absolute }, Cwd, Exists(absolute));

        CollectionAssert.AreEqual(new[] { absolute }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_Switch_IsUnchanged()
    {
        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { "--verbose", "-x" }, Cwd, _ => true);

        CollectionAssert.AreEqual(new[] { "--verbose", "-x" }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_BlankToken_IsUnchanged()
    {
        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { string.Empty, "  " }, Cwd, _ => true);

        CollectionAssert.AreEqual(new[] { string.Empty, "  " }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_NonExistentPath_IsUnchanged()
    {
        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { "missing.md" }, Cwd, _ => false);

        CollectionAssert.AreEqual(new[] { "missing.md" }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_ThrowingPredicate_LeavesArgumentUnchanged()
    {
        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(
            new[] { "README.md" },
            Cwd,
            _ => throw new IOException("boom"));

        CollectionAssert.AreEqual(new[] { "README.md" }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_MixedArguments_NormalizesOnlyExistingPaths()
    {
        var existing = Path.Combine(Cwd, "docs");

        var result = CommandLineArgumentNormalizer.ToAbsolutePaths(
            new[] { "-x", "docs", "missing" },
            Cwd,
            Exists(existing));

        CollectionAssert.AreEqual(new[] { "-x", existing, "missing" }, result);
    }

    [TestMethod]
    public void ToAbsolutePaths_NullPredicate_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => CommandLineArgumentNormalizer.ToAbsolutePaths(new[] { "." }, Cwd, null!));
    }

    [TestMethod]
    public void DiffersFrom_SameContents_ReturnsFalse()
    {
        Assert.IsFalse(CommandLineArgumentNormalizer.DiffersFrom(new[] { "a", "b" }, new[] { "a", "b" }));
    }

    [TestMethod]
    public void DiffersFrom_DifferentContents_ReturnsTrue()
    {
        Assert.IsTrue(CommandLineArgumentNormalizer.DiffersFrom(new[] { "." }, new[] { Cwd }));
    }

    [TestMethod]
    public void DiffersFrom_DifferentLength_ReturnsTrue()
    {
        Assert.IsTrue(CommandLineArgumentNormalizer.DiffersFrom(new[] { "a" }, Array.Empty<string>()));
    }

    [TestMethod]
    public void DiffersFrom_BothEmptyOrNull_ReturnsFalse()
    {
        Assert.IsFalse(CommandLineArgumentNormalizer.DiffersFrom(null, null));
        Assert.IsFalse(CommandLineArgumentNormalizer.DiffersFrom(null, Array.Empty<string>()));
        Assert.IsFalse(CommandLineArgumentNormalizer.DiffersFrom(Array.Empty<string>(), null));
    }

    [TestMethod]
    public void DiffersFrom_IsCaseSensitive()
    {
        Assert.IsTrue(CommandLineArgumentNormalizer.DiffersFrom(new[] { "a" }, new[] { "A" }));
    }
}
