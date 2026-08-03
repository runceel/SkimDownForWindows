using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkimDownForWindows.Application.CommandLine;

namespace SkimDownForWindows.Tests;

[TestClass]
public class CommandLineTokenizerTests
{
    [TestMethod]
    public void Tokenize_NullOrWhiteSpace_ReturnsEmpty()
    {
        Assert.IsEmpty(CommandLineTokenizer.Tokenize(null));
        Assert.IsEmpty(CommandLineTokenizer.Tokenize(string.Empty));
        Assert.IsEmpty(CommandLineTokenizer.Tokenize("   \t "));
    }

    [TestMethod]
    public void Tokenize_SplitsOnSpacesAndTabs()
    {
        var tokens = CommandLineTokenizer.Tokenize("a  b\tc");

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_QuotedPathWithSpaces_IsSingleToken()
    {
        var tokens = CommandLineTokenizer.Tokenize("\"C:\\My Docs\\a b.md\"");

        CollectionAssert.AreEqual(new[] { "C:\\My Docs\\a b.md" }, tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_ExePathAndQuotedArgument_AreSeparateTokens()
    {
        var tokens = CommandLineTokenizer.Tokenize("\"C:\\Program Files\\SkimDown\\skim.exe\" \"C:\\docs\\README.md\"");

        CollectionAssert.AreEqual(
            new[] { "C:\\Program Files\\SkimDown\\skim.exe", "C:\\docs\\README.md" },
            tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_TrailingBackslashesBeforeQuote_AreUnescaped()
    {
        // "C:\dir\\" -> C:\dir\  (2 backslashes before the closing quote collapse to 1)
        var tokens = CommandLineTokenizer.Tokenize("\"C:\\dir\\\\\"");

        CollectionAssert.AreEqual(new[] { "C:\\dir\\" }, tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_BackslashesNotBeforeQuote_ArePreserved()
    {
        var tokens = CommandLineTokenizer.Tokenize("C:\\a\\\\b\\c");

        CollectionAssert.AreEqual(new[] { "C:\\a\\\\b\\c" }, tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_OddBackslashesEscapeQuote()
    {
        // a\" -> a"
        var tokens = CommandLineTokenizer.Tokenize("a\\\"b");

        CollectionAssert.AreEqual(new[] { "a\"b" }, tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_DoubleQuoteInsideQuotes_IsLiteralQuote()
    {
        var tokens = CommandLineTokenizer.Tokenize("\"a\"\"b\"");

        CollectionAssert.AreEqual(new[] { "a\"b" }, tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_EmptyQuotedArgument_IsPreserved()
    {
        var tokens = CommandLineTokenizer.Tokenize("a \"\" b");

        CollectionAssert.AreEqual(new[] { "a", string.Empty, "b" }, tokens.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_DropsLeadingExeToken()
    {
        var targets = CommandLineTokenizer.ExtractPositionalTargets(
            "\"C:\\Program Files\\SkimDown\\SkimDownForWindows.exe\" C:\\docs");

        CollectionAssert.AreEqual(new[] { "C:\\docs" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_DropsLeadingExeTokenCaseInsensitively()
    {
        var targets = CommandLineTokenizer.ExtractPositionalTargets("skim.EXE C:\\docs\\a.md");

        CollectionAssert.AreEqual(new[] { "C:\\docs\\a.md" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_WithoutExeToken_KeepsAllPositionals()
    {
        var targets = CommandLineTokenizer.ExtractPositionalTargets("C:\\a C:\\b");

        CollectionAssert.AreEqual(new[] { "C:\\a", "C:\\b" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_DropsSwitchesAndBlanks()
    {
        var targets = CommandLineTokenizer.ExtractPositionalTargets("skim.exe --verbose \"\" -x C:\\docs");

        CollectionAssert.AreEqual(new[] { "C:\\docs" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_NullOrEmpty_ReturnsEmpty()
    {
        Assert.IsEmpty(CommandLineTokenizer.ExtractPositionalTargets(null));
        Assert.IsEmpty(CommandLineTokenizer.ExtractPositionalTargets("   "));
    }

    [TestMethod]
    public void Tokenize_DoubledQuoteInsideQuotes_ExitsQuotedState()
    {
        // CommandLineToArgvW と同じ規則: "" はリテラル " を出しつつクォートを抜ける。
        // クォート状態を維持すると以降のタブ/スペースがトークンに取り込まれてしまう。
        var tokens = CommandLineTokenizer.Tokenize(
            "\"C:\\Program Files\\Skim\\skim.exe\" \"\"\"::\t\t");

        CollectionAssert.AreEqual(
            new[] { "C:\\Program Files\\Skim\\skim.exe", "\"::" },
            tokens.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_DropsBareProgramStem_WhenProgramNamesSupplied()
    {
        // cmd.exe は `skim README.md` と入力された場合 argv[0] を "skim" のまま渡す。
        var targets = CommandLineTokenizer.ExtractPositionalTargets(
            "skim  README.md",
            new[] { "skim", "skimdown" });

        CollectionAssert.AreEqual(new[] { "README.md" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_DropsOnlyLeadingProgramToken_WhenTargetHasSameName()
    {
        // カレントに "skim" という名前のフォルダーがある場合でも、落とすのは argv[0] だけ。
        var targets = CommandLineTokenizer.ExtractPositionalTargets(
            "skim skim",
            new[] { "skim", "skimdown" });

        CollectionAssert.AreEqual(new[] { "skim" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_KeepsBareStem_WhenNotAProgramName()
    {
        var targets = CommandLineTokenizer.ExtractPositionalTargets(
            "skim.exe docs",
            new[] { "skim", "skimdown" });

        CollectionAssert.AreEqual(new[] { "docs" }, targets.ToArray());
    }

    [TestMethod]
    public void ExtractPositionalTargets_ExeOnly_ReturnsEmpty()
    {
        var targets = CommandLineTokenizer.ExtractPositionalTargets(
            "\"C:\\Program Files\\SkimDown\\SkimDownForWindows.exe\"");

        Assert.IsEmpty(targets);
    }
}
