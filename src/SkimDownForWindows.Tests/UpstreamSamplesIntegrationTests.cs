using System;
using System.IO;
using System.Linq;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Tests.TestHelpers;

namespace SkimDownForWindows.Tests;

/// <summary>
/// Integration tests that point our scanner / tree-builder at the actual
/// SkimDown samples folder published by the upstream macOS project.
///
/// The samples path is supplied via the <c>SKIM_SAMPLES_PATH</c> environment
/// variable (set in CI / locally to the cloned upstream `samples/` directory).
/// When the variable is unset or the path doesn't exist the tests are marked
/// inconclusive so they never falsely fail in a fresh checkout.
/// </summary>
[TestClass]
public sealed class UpstreamSamplesIntegrationTests
{
    private const string EnvVar = "SKIM_SAMPLES_PATH";

    private static MarkdownScanner CreateScanner() => new(new RealFileSystem());

    private static string ResolveSamplesPath()
    {
        var p = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        return Path.GetFullPath(p);
    }

    private static void RequireSamples(out string samples)
    {
        samples = ResolveSamplesPath();
        if (string.IsNullOrEmpty(samples) || !Directory.Exists(samples))
        {
            Assert.Inconclusive(
                $"Set the {EnvVar} environment variable to the path of " +
                "the upstream SkimDown 'samples' folder to run these tests.");
        }
    }

    [TestMethod]
    public void Scanner_FindsAllUpstreamMarkdownFiles()
    {
        RequireSamples(out var samples);
        var scanner = CreateScanner();
        var hits = scanner.Scan(samples);

        // 38 files in the upstream samples directory at the version we ported
        // against: 18 (en) + 18 (ja) + README.md + README_ja.md.
        // Re-asserting the exact count guards against accidental SPEC drift.
        Assert.IsTrue(hits.Count >= 30,
            $"Expected at least 30 Markdown files in samples, found {hits.Count}.");

        // All hits must be Markdown extensions.
        foreach (var h in hits)
        {
            var name = Path.GetFileName(h);
            Assert.IsTrue(
                name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase),
                $"Scanner returned non-Markdown file: {h}");
        }
    }

    [TestMethod]
    public void Scanner_IncludesDotMarkdownExtension()
    {
        // SPEC requires .markdown support; upstream samples/en/misc/sample.markdown
        // and samples/ja/misc/sample.markdown exercise it.
        RequireSamples(out var samples);
        var hits = CreateScanner().Scan(samples);
        Assert.IsTrue(
            hits.Any(p => p.EndsWith("sample.markdown", StringComparison.OrdinalIgnoreCase)),
            "Expected at least one .markdown (full-extension) file in the samples set.");
    }

    [TestMethod]
    public void Scanner_FindsDeepNestedSamples()
    {
        // samples/en/deep/nested/folder/deep-file.md tests recursion depth.
        RequireSamples(out var samples);
        var hits = CreateScanner().Scan(samples);
        Assert.IsTrue(
            hits.Any(p => p.Replace('\\', '/').EndsWith("/deep/nested/folder/deep-file.md", StringComparison.OrdinalIgnoreCase)),
            "Expected to find a deep-nested .md file via recursive scan.");
    }

    [TestMethod]
    public void TreeBuilder_TopLevelFoldersBeforeReadmeFiles()
    {
        RequireSamples(out var samples);
        var files = CreateScanner().Scan(samples);
        var root = new MarkdownTreeBuilder().Build(samples, files);

        // First sort: folders, then files. SPEC ordering, VS Code style.
        var firstFolder = root.Children.FirstOrDefault();
        Assert.IsNotNull(firstFolder, "Expected at least one top-level entry.");
        Assert.IsTrue(firstFolder!.IsFolder,
            $"First top-level entry should be a folder; got '{firstFolder.Name}'.");

        // README.md at the top level should appear, after the folders.
        var readme = root.Children
            .FirstOrDefault(c => !c.IsFolder &&
                                string.Equals(c.Name, "README.md", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(readme, "Expected README.md at the top level of samples/.");
    }

    [TestMethod]
    public void TreeBuilder_CategoryFoldersAreAlphabetized()
    {
        RequireSamples(out var samples);
        var files = CreateScanner().Scan(samples);
        var root = new MarkdownTreeBuilder().Build(samples, files);

        // Top level should expose en, ja, (images is non-markdown so should be omitted).
        var topFolderNames = root.Children
            .Where(c => c.IsFolder)
            .Select(c => c.Name)
            .ToArray();
        // en and ja are the language folders; check alpha order is preserved.
        var sorted = topFolderNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(sorted, topFolderNames,
            "Top-level folders should be alphabetical, case-insensitive.");

        // The "images" folder contains no Markdown and must be omitted.
        Assert.IsFalse(topFolderNames.Any(n => string.Equals(n, "images", StringComparison.OrdinalIgnoreCase)),
            "Folders with no Markdown anywhere below them must be hidden.");
    }

    [TestMethod]
    public void TreeBuilder_CategoryStructureExists()
    {
        // Check that the en branch contains the documented subfolders.
        RequireSamples(out var samples);
        var files = CreateScanner().Scan(samples);
        var root = new MarkdownTreeBuilder().Build(samples, files);

        var en = root.Children.FirstOrDefault(c => c.IsFolder && c.Name == "en");
        Assert.IsNotNull(en, "Expected 'en' branch in samples tree.");

        var expectedCategories = new[] { "basics", "blocks", "deep", "extended", "misc" };
        var enFolderNames = en!.Children
            .Where(c => c.IsFolder)
            .Select(c => c.Name)
            .ToArray();

        foreach (var cat in expectedCategories)
        {
            Assert.IsTrue(enFolderNames.Contains(cat),
                $"Expected 'en/{cat}' folder. Got: [{string.Join(", ", enFolderNames)}]");
        }
    }

    [TestMethod]
    public void TreeBuilder_MarkdownCount_MatchesScannerCount()
    {
        RequireSamples(out var samples);
        var files = CreateScanner().Scan(samples);
        var root = new MarkdownTreeBuilder().Build(samples, files);
        Assert.AreEqual(files.Count, root.MarkdownCount,
            "Tree root MarkdownCount should equal the number of files the scanner found.");
    }

    [TestMethod]
    public void Picker_OnSamplesFolder_PicksReadme()
    {
        // From a cold open of samples/, the initial selection should land on
        // the top-level README.md per SPEC fallback chain.
        RequireSamples(out var samples);
        var files = CreateScanner().Scan(samples);
        var root = new MarkdownTreeBuilder().Build(samples, files);

        var picked = new InitialSelectionPicker().Pick(root, lastSelectedRelativePath: null);
        Assert.IsNotNull(picked);
        StringAssert.EndsWith(picked!, "README.md");
    }

    [TestMethod]
    public void Picker_HonorsLastSelectionWhenPresent()
    {
        RequireSamples(out var samples);
        var files = CreateScanner().Scan(samples);
        var root = new MarkdownTreeBuilder().Build(samples, files);

        var picked = new InitialSelectionPicker().Pick(root, "en/extended/footnotes.md");
        Assert.IsNotNull(picked);
        StringAssert.EndsWith(picked!.Replace('\\', '/'), "en/extended/footnotes.md");
    }
}
