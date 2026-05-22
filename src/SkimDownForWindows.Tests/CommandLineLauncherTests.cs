using System;
using System.IO;
using SkimDownForWindows.Core;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class CommandLineLauncherTests
{
    private string _tempRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SkimDownCLI_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void Returns_Null_When_No_Args()
    {
        Assert.IsNull(CommandLineLauncher.TryGetInitialFolderPath(Array.Empty<string>(), _tempRoot));
        Assert.IsNull(CommandLineLauncher.TryGetInitialFolderPath(new[] { "skimdown.exe" }, _tempRoot));
    }

    [TestMethod]
    public void Returns_Null_When_Args_Is_Null()
    {
        Assert.IsNull(CommandLineLauncher.TryGetInitialFolderPath(null!, _tempRoot));
    }

    [TestMethod]
    public void Returns_Folder_When_First_Arg_Is_Existing_Absolute_Folder()
    {
        var folder = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(folder);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", folder },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.AreEqual(
            Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            result!.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }

    [TestMethod]
    public void Returns_Folder_When_First_Arg_Is_Relative_Folder()
    {
        var folder = Path.Combine(_tempRoot, "notes");
        Directory.CreateDirectory(folder);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", "notes" },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase),
            $"Expected '{folder}', got '{result}'");
    }

    [TestMethod]
    public void Returns_Dot_As_Current_Directory()
    {
        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", "." },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(_tempRoot), StringComparison.OrdinalIgnoreCase),
            $"Expected '{_tempRoot}', got '{result}'");
    }

    [TestMethod]
    public void Returns_Null_When_Path_Does_Not_Exist()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", missing },
            currentDirectory: _tempRoot);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Returns_Null_When_Path_Is_Invalid()
    {
        // A path that GetFullPath rejects on Windows (control chars in name).
        var bad = "\0bad-path";
        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", bad },
            currentDirectory: _tempRoot);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Returns_Parent_Folder_When_Arg_Is_Markdown_File()
    {
        var folder = Path.Combine(_tempRoot, "proj");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "README.md");
        File.WriteAllText(file, "# hi");

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", file },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase),
            $"Expected parent '{folder}', got '{result}'");
    }

    [TestMethod]
    public void Returns_Parent_Folder_When_Arg_Is_Markdown_File_Dot_Markdown_Extension()
    {
        var folder = Path.Combine(_tempRoot, "longext");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "note.markdown");
        File.WriteAllText(file, "# hi");

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", file },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Returns_Null_When_Arg_Is_Non_Markdown_File()
    {
        var folder = Path.Combine(_tempRoot, "txt");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "hi");

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", file },
            currentDirectory: _tempRoot);

        // .txt should not be accepted — the app only reads markdown.
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Skips_Switch_Args_And_Picks_Next_Positional()
    {
        var folder = Path.Combine(_tempRoot, "after-switch");
        Directory.CreateDirectory(folder);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", "--verbose", "-x", folder },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Skips_Empty_And_Whitespace_Args()
    {
        var folder = Path.Combine(_tempRoot, "post-blanks");
        Directory.CreateDirectory(folder);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", "", "   ", folder },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Picks_First_Folder_When_Multiple_Folders_Provided()
    {
        var first = Path.Combine(_tempRoot, "first");
        var second = Path.Combine(_tempRoot, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", first, second },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Falls_Through_Missing_Then_Picks_Existing_Folder()
    {
        // First positional arg is non-existent, second is a real folder. The
        // parser should advance past the bad one (per current behavior:
        // "first usable" wins). NOTE: this test documents the current
        // forgiving behavior; if we ever decide a missing-but-positional
        // arg should *fail closed*, update this test together with the impl.
        var good = Path.Combine(_tempRoot, "good");
        Directory.CreateDirectory(good);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", Path.Combine(_tempRoot, "ghost"), good },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).Equals(Path.GetFullPath(good), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Accepts_Folder_With_Trailing_Separator()
    {
        var folder = Path.Combine(_tempRoot, "trailing");
        Directory.CreateDirectory(folder);

        var result = CommandLineLauncher.TryGetInitialFolderPath(
            new[] { "skimdown.exe", folder + Path.DirectorySeparatorChar },
            currentDirectory: _tempRoot);

        Assert.IsNotNull(result);
        Assert.IsTrue(
            Path.GetFullPath(result!).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }
}
