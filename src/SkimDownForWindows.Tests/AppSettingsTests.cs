using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Tests;

/// <summary>
/// <see cref="AppSettings"/> の永続化ロジック (recent folders / folder states) を検証する。
/// JSON ラウンドトリップでフィールド保持も確認する。シリアライザ自体 (JsonSettingsRepository) は
/// Infrastructure 層のためここでは扱わない。
/// </summary>
[TestClass]
public sealed class AppSettingsTests
{
    [TestMethod]
    public void UpdateRecentFolders_AddsNewEntry_AtFront_AndUpdatesLastFolderPath()
    {
        var s = new AppSettings();

        s.UpdateRecentFolders(@"C:\docs");

        Assert.HasCount(1, s.RecentFolders);
        Assert.AreEqual(@"C:\docs", s.RecentFolders[0]);
        Assert.AreEqual(@"C:\docs", s.LastFolderPath);
    }

    [TestMethod]
    public void UpdateRecentFolders_MultipleAdds_ResultsInMostRecentFirst()
    {
        var s = new AppSettings();

        s.UpdateRecentFolders(@"C:\a");
        s.UpdateRecentFolders(@"C:\b");
        s.UpdateRecentFolders(@"C:\c");

        CollectionAssert.AreEqual(new[] { @"C:\c", @"C:\b", @"C:\a" }, s.RecentFolders);
        Assert.AreEqual(@"C:\c", s.LastFolderPath);
    }

    [TestMethod]
    public void UpdateRecentFolders_DuplicatePath_RemovesPriorAndInsertsAtFront()
    {
        var s = new AppSettings();
        s.UpdateRecentFolders(@"C:\a");
        s.UpdateRecentFolders(@"C:\b");

        s.UpdateRecentFolders(@"C:\a");

        CollectionAssert.AreEqual(new[] { @"C:\a", @"C:\b" }, s.RecentFolders);
        Assert.AreEqual(@"C:\a", s.LastFolderPath);
    }

    [TestMethod]
    public void UpdateRecentFolders_DifferentCase_IsTreatedAsDuplicate()
    {
        var s = new AppSettings();
        s.UpdateRecentFolders(@"C:\Docs");

        s.UpdateRecentFolders(@"c:\docs");

        Assert.HasCount(1, s.RecentFolders);
        Assert.AreEqual(@"c:\docs", s.RecentFolders[0]);
        Assert.AreEqual(@"c:\docs", s.LastFolderPath);
    }

    [TestMethod]
    public void UpdateRecentFolders_OverMaxLimit_TruncatesOldest()
    {
        var s = new AppSettings();
        for (var i = 0; i < AppSettings.MaxRecentFolders + 5; i++)
        {
            s.UpdateRecentFolders($@"C:\folder{i:D2}");
        }

        Assert.HasCount(AppSettings.MaxRecentFolders, s.RecentFolders);
        // 最後に追加した folder20 が先頭、初期に追加した folder0..4 (5 件) は除外される。
        Assert.AreEqual($@"C:\folder{AppSettings.MaxRecentFolders + 4:D2}", s.RecentFolders[0]);
        Assert.AreEqual($@"C:\folder{AppSettings.MaxRecentFolders + 4:D2}", s.LastFolderPath);
        Assert.DoesNotContain(@"C:\folder00", s.RecentFolders);
    }

    [TestMethod]
    public void UpdateRecentFolders_EmptyOrWhitespace_IsNoOp()
    {
        var s = new AppSettings();
        s.UpdateRecentFolders(@"C:\real");

        s.UpdateRecentFolders("");
        s.UpdateRecentFolders("   ");
        s.UpdateRecentFolders(null!);

        Assert.HasCount(1, s.RecentFolders);
        Assert.AreEqual(@"C:\real", s.RecentFolders[0]);
        Assert.AreEqual(@"C:\real", s.LastFolderPath);
    }

    [TestMethod]
    public void GetOrCreateFolderState_FirstCall_CreatesNewState()
    {
        var s = new AppSettings();

        var state = s.GetOrCreateFolderState(@"C:\docs");

        Assert.IsNotNull(state);
        Assert.IsNull(state.LastSelectedRelativePath);
        Assert.IsEmpty(state.ExpandedFolders);
        Assert.IsTrue(s.FolderStates.ContainsKey(@"C:\docs"));
    }

    [TestMethod]
    public void GetOrCreateFolderState_SecondCall_ReturnsSameInstance()
    {
        var s = new AppSettings();

        var a = s.GetOrCreateFolderState(@"C:\docs");
        a.LastSelectedRelativePath = "README.md";
        var b = s.GetOrCreateFolderState(@"C:\docs");

        Assert.AreSame(a, b);
        Assert.AreEqual("README.md", b.LastSelectedRelativePath);
    }

    /// <summary>
    /// 現挙動の golden test: <see cref="AppSettings.FolderStates"/> は通常 <c>Dictionary</c> のため
    /// case-sensitive。Windows のフォルダーパス慣習からすると望ましくないが現実装の固定化。
    /// 仕様変更時は別 ADR + PR で扱う。
    /// </summary>
    [TestMethod]
    public void GetOrCreateFolderState_SamePathDifferentCase_DocumentsCurrentBehavior()
    {
        var s = new AppSettings();

        var lower = s.GetOrCreateFolderState(@"c:\docs");
        var upper = s.GetOrCreateFolderState(@"C:\DOCS");

        Assert.AreNotSame(lower, upper,
            "AppSettings.FolderStates は現状 case-sensitive。仕様修正時はこのテストを更新する。");
        Assert.HasCount(2, s.FolderStates);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesAllFields()
    {
        var original = new AppSettings
        {
            Theme = AppTheme.Dark,
            ZoomFactor = 1.25,
            SearchCaseSensitive = true,
            SidebarWidth = 320,
            SidebarVisible = false,
            SidebarPosition = SidebarPosition.Right,
            ContentMaxWidth = ContentMaxWidth.ExtraWide,
            RecentFolders = new List<string> { @"C:\a", @"C:\b" },
            LastFolderPath = @"C:\a",
            FolderStates = new Dictionary<string, FolderState>
            {
                [@"C:\a"] = new FolderState
                {
                    LastSelectedRelativePath = "docs/intro.md",
                    ExpandedFolders = new List<string> { "docs", "docs/deep" },
                },
            },
        };

        var json = JsonSerializer.Serialize(original);
        var round = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.IsNotNull(round);
        Assert.AreEqual(AppTheme.Dark, round!.Theme);
        Assert.AreEqual(1.25, round.ZoomFactor);
        Assert.IsTrue(round.SearchCaseSensitive);
        Assert.AreEqual(320, round.SidebarWidth);
        Assert.IsFalse(round.SidebarVisible);
        Assert.AreEqual(SidebarPosition.Right, round.SidebarPosition);
        Assert.AreEqual(ContentMaxWidth.ExtraWide, round.ContentMaxWidth);
        CollectionAssert.AreEqual(new[] { @"C:\a", @"C:\b" }, round.RecentFolders);
        Assert.AreEqual(@"C:\a", round.LastFolderPath);

        Assert.IsTrue(round.FolderStates.ContainsKey(@"C:\a"));
        var state = round.FolderStates[@"C:\a"];
        Assert.AreEqual("docs/intro.md", state.LastSelectedRelativePath);
        CollectionAssert.AreEqual(new[] { "docs", "docs/deep" }, state.ExpandedFolders);
    }

    [TestMethod]
    public void Defaults_AreSpecCompliant()
    {
        var s = new AppSettings();

        Assert.AreEqual(AppTheme.System, s.Theme);
        Assert.IsNull(s.CustomThemeId);
        Assert.AreEqual(1.0, s.ZoomFactor);
        Assert.IsFalse(s.SearchCaseSensitive);
        Assert.AreEqual(280d, s.SidebarWidth);
        Assert.IsTrue(s.SidebarVisible);
        Assert.AreEqual(SidebarPosition.Left, s.SidebarPosition);
        Assert.AreEqual(ContentMaxWidth.Full, s.ContentMaxWidth);
        Assert.IsEmpty(s.RecentFolders);
        Assert.IsNull(s.LastFolderPath);
        Assert.IsEmpty(s.FolderStates);
    }

    [TestMethod]
    public void NormalizeAfterLoad_KeepsBuiltInThemes()
    {
        var s = new AppSettings { Theme = AppTheme.Dark, CustomThemeId = "ignored" };
        s.NormalizeAfterLoad();
        Assert.AreEqual(AppTheme.Dark, s.Theme);
        Assert.IsNull(s.CustomThemeId);
    }

    [TestMethod]
    public void NormalizeAfterLoad_DropsInvalidCustomTheme()
    {
        var s = new AppSettings { Theme = AppTheme.Custom, CustomThemeId = null };
        s.NormalizeAfterLoad();
        Assert.AreEqual(AppTheme.System, s.Theme);
        Assert.IsNull(s.CustomThemeId);

        s = new AppSettings { Theme = AppTheme.Custom, CustomThemeId = string.Empty };
        s.NormalizeAfterLoad();
        Assert.AreEqual(AppTheme.System, s.Theme);
    }

    [TestMethod]
    public void NormalizeAfterLoad_PreservesValidCustomTheme()
    {
        var s = new AppSettings { Theme = AppTheme.Custom, CustomThemeId = "monokai" };
        s.NormalizeAfterLoad();
        Assert.AreEqual(AppTheme.Custom, s.Theme);
        Assert.AreEqual("monokai", s.CustomThemeId);
    }

    [TestMethod]
    public void NormalizeAfterLoad_ClampsUnknownContentMaxWidth_ToStandard()
    {
        // 永続化された値が将来追加された enum 値で、現バージョンの定義範囲外だった場合
        // (= ユーザーが新→旧バージョンにダウングレードしたケース) は安全側として Standard に戻す。
        var s = new AppSettings { ContentMaxWidth = (ContentMaxWidth)999 };

        s.NormalizeAfterLoad();

        Assert.AreEqual(ContentMaxWidth.Standard, s.ContentMaxWidth);
    }

    [TestMethod]
    public void NormalizeAfterLoad_PreservesValidContentMaxWidth()
    {
        var s = new AppSettings { ContentMaxWidth = ContentMaxWidth.Wide };

        s.NormalizeAfterLoad();

        Assert.AreEqual(ContentMaxWidth.Wide, s.ContentMaxWidth);
    }
}
