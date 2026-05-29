using System;
using System.IO;
using SkimDownForWindows.Application.CommandLine;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Tests.TestHelpers;

namespace SkimDownForWindows.Tests;

[TestClass]
public sealed class CommandLineLauncherTests
{
    private string _tempRoot = null!;
    private CommandLineLauncher _launcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SkimDownCLI_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _launcher = new CommandLineLauncher(new RealFileSystem());
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    // ----- TryResolveActivation -----

    [TestMethod]
    public void TryResolveActivation_Returns_Null_When_No_Args()
    {
        Assert.IsNull(_launcher.TryResolveActivation(Array.Empty<string>(), _tempRoot));
        Assert.IsNull(_launcher.TryResolveActivation(new[] { "skimdown.exe" }, _tempRoot));
    }

    [TestMethod]
    public void TryResolveActivation_Returns_Null_When_Args_Is_Null()
    {
        Assert.IsNull(_launcher.TryResolveActivation(null!, _tempRoot));
    }

    [TestMethod]
    public void TryResolveActivation_Returns_OpenFolderActivation_When_First_Arg_Is_Existing_Absolute_Folder()
    {
        var folder = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(folder);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", folder },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.AreEqual(
            Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            open!.FolderPath.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }

    [TestMethod]
    public void TryResolveActivation_Returns_OpenFolderActivation_When_First_Arg_Is_Relative_Folder()
    {
        var folder = Path.Combine(_tempRoot, "notes");
        Directory.CreateDirectory(folder);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", "notes" },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase),
            $"Expected '{folder}', got '{open.FolderPath}'");
    }

    [TestMethod]
    public void TryResolveActivation_Returns_OpenFolderActivation_For_Dot_As_Current_Directory()
    {
        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", "." },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(_tempRoot), StringComparison.OrdinalIgnoreCase),
            $"Expected '{_tempRoot}', got '{open.FolderPath}'");
    }

    [TestMethod]
    public void TryResolveActivation_Returns_Null_When_Path_Does_Not_Exist()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", missing },
            currentDirectory: _tempRoot);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryResolveActivation_Returns_Null_When_Path_Is_Invalid()
    {
        // A path that GetFullPath rejects on Windows (control chars in name).
        var bad = "\0bad-path";
        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", bad },
            currentDirectory: _tempRoot);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryResolveActivation_Returns_OpenSingleFileActivation_When_Arg_Is_Markdown_File()
    {
        var folder = Path.Combine(_tempRoot, "proj");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "README.md");
        File.WriteAllText(file, "# hi");

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", file },
            currentDirectory: _tempRoot);

        var open = result as OpenSingleFileActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FilePath).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase),
            $"Expected file '{file}', got '{open.FilePath}'");
    }

    [TestMethod]
    public void TryResolveActivation_Returns_OpenSingleFileActivation_For_Dot_Markdown_Extension()
    {
        var folder = Path.Combine(_tempRoot, "longext");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "note.markdown");
        File.WriteAllText(file, "# hi");

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", file },
            currentDirectory: _tempRoot);

        var open = result as OpenSingleFileActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FilePath).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryResolveActivation_Returns_Null_When_Arg_Is_Non_Markdown_File()
    {
        var folder = Path.Combine(_tempRoot, "txt");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "hi");

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", file },
            currentDirectory: _tempRoot);

        // .txt should not be accepted — the app only reads markdown.
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryResolveActivation_Skips_Switch_Args_And_Picks_Next_Positional()
    {
        var folder = Path.Combine(_tempRoot, "after-switch");
        Directory.CreateDirectory(folder);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", "--verbose", "-x", folder },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryResolveActivation_Skips_Empty_And_Whitespace_Args()
    {
        var folder = Path.Combine(_tempRoot, "post-blanks");
        Directory.CreateDirectory(folder);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", "", "   ", folder },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryResolveActivation_Picks_First_Folder_When_Multiple_Folders_Provided()
    {
        var first = Path.Combine(_tempRoot, "first");
        var second = Path.Combine(_tempRoot, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", first, second },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryResolveActivation_Falls_Through_Missing_Then_Picks_Existing_Folder()
    {
        // First positional arg is non-existent, second is a real folder.
        var good = Path.Combine(_tempRoot, "good");
        Directory.CreateDirectory(good);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", Path.Combine(_tempRoot, "ghost"), good },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(good), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryResolveActivation_Accepts_Folder_With_Trailing_Separator()
    {
        var folder = Path.Combine(_tempRoot, "trailing");
        Directory.CreateDirectory(folder);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", folder + Path.DirectorySeparatorChar },
            currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryResolveActivation_Picks_First_Usable_When_Folder_And_File_Mixed()
    {
        var file = Path.Combine(_tempRoot, "first.md");
        File.WriteAllText(file, "# x");
        var folder = Path.Combine(_tempRoot, "second-folder");
        Directory.CreateDirectory(folder);

        var result = _launcher.TryResolveActivation(
            new[] { "skimdown.exe", file, folder },
            currentDirectory: _tempRoot);

        // First arg is a markdown file -> OpenSingleFileActivation wins.
        var open = result as OpenSingleFileActivation;
        Assert.IsNotNull(open, $"Expected OpenSingleFileActivation, got {result?.GetType().Name ?? "null"}");
    }

    // ----- Classify -----

    [TestMethod]
    public void Classify_Returns_OpenFolderActivation_For_Existing_Directory()
    {
        var folder = Path.Combine(_tempRoot, "dir");
        Directory.CreateDirectory(folder);

        var result = _launcher.Classify(folder, currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Classify_Returns_OpenSingleFileActivation_For_Existing_Markdown_File()
    {
        var file = Path.Combine(_tempRoot, "README.md");
        File.WriteAllText(file, "# hi");

        var result = _launcher.Classify(file, currentDirectory: _tempRoot);

        var open = result as OpenSingleFileActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FilePath).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Classify_Returns_Null_For_Non_Markdown_File()
    {
        var file = Path.Combine(_tempRoot, "notes.txt");
        File.WriteAllText(file, "hi");

        var result = _launcher.Classify(file, currentDirectory: _tempRoot);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Classify_Returns_Null_For_Missing_Path()
    {
        var missing = Path.Combine(_tempRoot, "ghost");
        var result = _launcher.Classify(missing, currentDirectory: _tempRoot);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Classify_Returns_Null_For_Null_Or_Whitespace()
    {
        Assert.IsNull(_launcher.Classify("", currentDirectory: _tempRoot));
        Assert.IsNull(_launcher.Classify("   ", currentDirectory: _tempRoot));
        Assert.IsNull(_launcher.Classify(null!, currentDirectory: _tempRoot));
    }

    [TestMethod]
    public void Classify_Returns_Null_For_Invalid_Path()
    {
        var result = _launcher.Classify("\0bad", currentDirectory: _tempRoot);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Classify_Resolves_Relative_Path_Against_CurrentDirectory()
    {
        var folder = Path.Combine(_tempRoot, "rel");
        Directory.CreateDirectory(folder);

        var result = _launcher.Classify("rel", currentDirectory: _tempRoot);

        var open = result as OpenFolderActivation;
        Assert.IsNotNull(open);
        Assert.IsTrue(
            Path.GetFullPath(open!.FolderPath).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
    }
}
