using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Theme;
using SkimDownForWindows.Application.ViewModels;
using SkimDownForWindows.Domain;
using SkimDownForWindows.Tests.TestHelpers;

namespace SkimDownForWindows.Tests;

/// <summary>
/// <see cref="MainPageViewModel"/> の coordinator 振る舞いを検証する。
///
/// 方針:
/// - 純粋サービス (<see cref="MarkdownScanner"/> / <see cref="MarkdownTreeBuilder"/> /
///   <see cref="InitialSelectionPicker"/> / <see cref="LinkResolver"/>) は実体を使用する。
/// - 外部 I/O 抽象 (settings / watcher / reader / shell / clipboard / theme) はテスト用
///   in-memory ダブルで差し替え、観察可能な副作用を検証する。
/// - <c>async void</c> ハンドラの完了は <see cref="InMemorySettingsRepository.WaitForSaveCountAsync"/>
///   で同期する。Sleep / Delay は使用しない。
/// </summary>
[TestClass]
public sealed class MainPageViewModelTests
{
    private string _root = null!;
    private InMemorySettingsRepository _settings = null!;
    private FakeFolderWatcher _watcher = null!;
    private StubMarkdownFileReader _reader = null!;
    private RecordingShellService _shell = null!;
    private RecordingClipboardService _clipboard = null!;
    private StubSystemThemeProvider _theme = null!;
    private RealFileSystem _fs = null!;
    private InMemoryColorSchemeSource _colorSchemeSource = null!;
    private ColorSchemeRegistry _colorSchemes = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "skim-vm-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);

        _settings = new InMemorySettingsRepository();
        _watcher = new FakeFolderWatcher();
        _reader = new StubMarkdownFileReader();
        _shell = new RecordingShellService();
        _clipboard = new RecordingClipboardService();
        _theme = new StubSystemThemeProvider();
        _fs = new RealFileSystem();
        _colorSchemeSource = new InMemoryColorSchemeSource();
        _colorSchemes = new ColorSchemeRegistry(_colorSchemeSource);
    }

    [TestCleanup]
    public void Teardown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private MainPageViewModel CreateViewModel()
    {
        var scanner = new MarkdownScanner(_fs);
        var treeBuilder = new MarkdownTreeBuilder();
        var picker = new InitialSelectionPicker();
        var linkResolver = new LinkResolver();
        return new MainPageViewModel(
            _settings,
            _watcher,
            _reader,
            _fs,
            _shell,
            _clipboard,
            _theme,
            _colorSchemes,
            scanner,
            treeBuilder,
            picker,
            linkResolver);
    }

    private void Touch(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string AbsoluteRoot() => Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar);

    // ----- OpenFolderAsync ------------------------------------------------

    [TestMethod]
    public async Task OpenFolderAsync_SetsFolderMetadata_AndBuildsTree()
    {
        Touch("README.md");
        Touch("docs/intro.md");
        var vm = CreateViewModel();

        await vm.OpenFolderAsync(_root);

        Assert.IsTrue(vm.HasFolder);
        Assert.IsTrue(vm.HasAnyMarkdown);
        Assert.AreEqual(2, vm.MarkdownCount);
        Assert.AreEqual(AbsoluteRoot(), vm.OpenedFolderPath);
        Assert.AreEqual(Path.GetFileName(AbsoluteRoot()), vm.OpenedFolderName);
        Assert.AreEqual($"{vm.OpenedFolderName} — SkimDown", vm.WindowTitle);
        Assert.HasCount(2, vm.RootItems); // docs フォルダー + README.md
    }

    [TestMethod]
    public async Task OpenFolderAsync_PicksInitialFile_AndFiresPreviewLoadRequested()
    {
        Touch("README.md", "# hello");
        Touch("notes.md", "# notes");
        var vm = CreateViewModel();

        LoadRequest? captured = null;
        vm.PreviewLoadRequested += r => captured = r;
        _reader.SetContent(Path.Combine(AbsoluteRoot(), "README.md"), "# hello");

        await vm.OpenFolderAsync(_root);

        Assert.IsNotNull(captured);
        Assert.AreEqual("# hello", captured!.Markdown);
        Assert.AreEqual("README.md", captured.RelativePath);
        Assert.AreEqual("light", captured.Theme);
        Assert.IsNotNull(vm.SelectedItem);
        Assert.AreEqual("README.md", vm.SelectedItem!.Name);
    }

    [TestMethod]
    public async Task OpenFolderAsync_EmptyFolder_ClearsPreview_AndDoesNotFireLoad()
    {
        // No markdown files at all.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var vm = CreateViewModel();

        var loadFired = 0;
        var clearFired = 0;
        vm.PreviewLoadRequested += _ => loadFired++;
        vm.PreviewClearRequested += () => clearFired++;

        await vm.OpenFolderAsync(_root);

        Assert.IsTrue(vm.HasFolder);
        Assert.IsFalse(vm.HasAnyMarkdown);
        Assert.AreEqual(0, vm.MarkdownCount);
        Assert.IsNull(vm.SelectedItem);
        Assert.AreEqual(0, loadFired);
        Assert.AreEqual(1, clearFired);
        // 空でも recent 更新で 1 回 SaveAsync。
        Assert.AreEqual(1, _settings.SaveAsyncCalls);
    }

    [TestMethod]
    public async Task OpenFolderAsync_NonExistentFolder_IsNoOp()
    {
        var vm = CreateViewModel();
        var missing = Path.Combine(_root, "does-not-exist");

        await vm.OpenFolderAsync(missing);

        Assert.IsFalse(vm.HasFolder);
        Assert.IsNull(vm.OpenedFolderPath);
        Assert.AreEqual(0, _settings.SaveAsyncCalls);
        Assert.AreEqual(0, _watcher.WatchCalls);
    }

    [TestMethod]
    public async Task OpenFolderAsync_RestoresExpandedFolders_FromSettings()
    {
        // README.md を置くことで初期選択がルートに留まり、ExpandAncestors の auto-expand
        // が「設定からの復元」と混在しない。
        Touch("README.md");
        Touch("docs/intro.md");
        Touch("docs/deep/leaf.md");
        Touch("api/spec.md");
        _settings.Current.GetOrCreateFolderState(AbsoluteRoot()).ExpandedFolders =
            new List<string> { "docs", "docs/deep" };

        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);

        var docs = vm.RootItems.Single(r => r.Name == "docs");
        Assert.IsTrue(docs.IsExpanded);
        var deep = docs.Children.Single(c => c.Name == "deep");
        Assert.IsTrue(deep.IsExpanded);
        var api = vm.RootItems.Single(r => r.Name == "api");
        Assert.IsFalse(api.IsExpanded);
    }

    [TestMethod]
    public async Task OpenFolderAsync_WithInitialSelection_PersistsTwice()
    {
        // README で SaveAsync が呼ばれ、その後 recent 更新でもう 1 回。合計 2 回。
        Touch("README.md");
        var vm = CreateViewModel();

        await vm.OpenFolderAsync(_root);

        Assert.AreEqual(2, _settings.SaveAsyncCalls);
        // RecentFolders は更新されている。
        Assert.HasCount(1, _settings.Current.RecentFolders);
        Assert.AreEqual(AbsoluteRoot(), _settings.Current.RecentFolders[0]);
        Assert.AreEqual(AbsoluteRoot(), _settings.Current.LastFolderPath);
        // ViewModel 側の最近フォルダーリストも反映。
        Assert.HasCount(1, vm.RecentFolders);
        Assert.AreEqual(AbsoluteRoot(), vm.RecentFolders[0].FullPath);
    }

    [TestMethod]
    public async Task OpenFolderAsync_StartsFolderWatcher_WithCanonicalPath()
    {
        Touch("README.md");
        var vm = CreateViewModel();

        await vm.OpenFolderAsync(_root + Path.DirectorySeparatorChar); // trailing sep

        Assert.AreEqual(1, _watcher.WatchCalls);
        Assert.AreEqual(AbsoluteRoot(), _watcher.LastWatchedPath);
    }

    [TestMethod]
    public async Task OpenFolderAsync_RaisesPropertyChanged_ForKeyBindings()
    {
        Touch("README.md");
        var vm = CreateViewModel();

        var changed = new HashSet<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changed.Add(e.PropertyName); };

        await vm.OpenFolderAsync(_root);

        Assert.Contains(nameof(MainPageViewModel.HasFolder), changed);
        Assert.Contains(nameof(MainPageViewModel.HasAnyMarkdown), changed);
        Assert.Contains(nameof(MainPageViewModel.MarkdownCount), changed);
        Assert.Contains(nameof(MainPageViewModel.WindowTitle), changed);
        Assert.Contains(nameof(MainPageViewModel.OpenedFolderPath), changed);
        Assert.Contains(nameof(MainPageViewModel.OpenedFolderName), changed);
    }

    // ----- SelectAndLoadAsync ---------------------------------------------

    [TestMethod]
    public async Task SelectAndLoadAsync_OutsideFolder_IsNoOp()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        var savesBefore = _settings.SaveAsyncCalls;

        var fired = 0;
        vm.PreviewLoadRequested += _ => fired++;
        var outside = Path.Combine(Path.GetTempPath(), "other-folder-" + Guid.NewGuid().ToString("N"), "x.md");

        await vm.SelectAndLoadAsync(outside);

        Assert.AreEqual(0, fired, "外側ファイルは PreviewLoadRequested を発火しない");
        Assert.AreEqual(savesBefore, _settings.SaveAsyncCalls);
    }

    [TestMethod]
    public async Task SelectAndLoadAsync_FiresPreviewLoadRequested_WithEffectiveTheme()
    {
        Touch("README.md", "# r");
        Touch("notes.md", "# notes");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        _theme.System = AppTheme.Dark;
        _settings.Current.Theme = AppTheme.System;
        var notesFull = Path.Combine(AbsoluteRoot(), "notes.md");
        _reader.SetContent(notesFull, "# notes-body");

        LoadRequest? captured = null;
        vm.PreviewLoadRequested += r => captured = r;

        await vm.SelectAndLoadAsync(notesFull);

        Assert.IsNotNull(captured);
        Assert.AreEqual("# notes-body", captured!.Markdown);
        Assert.AreEqual("notes.md", captured.RelativePath);
        Assert.AreEqual("dark", captured.Theme);
        Assert.IsNotNull(vm.SelectedItem);
        Assert.AreEqual("notes.md", vm.SelectedItem!.Name);
    }

    [TestMethod]
    public async Task SelectAndLoadAsync_PersistsLastSelected()
    {
        Touch("README.md");
        Touch("notes.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        var notesFull = Path.Combine(AbsoluteRoot(), "notes.md");

        await vm.SelectAndLoadAsync(notesFull);

        var state = _settings.Current.GetOrCreateFolderState(AbsoluteRoot());
        Assert.AreEqual("notes.md", state.LastSelectedRelativePath);
    }

    /// <summary>
    /// 現挙動の golden test: <see cref="MainPageViewModel.SelectAndLoadAsync"/> はツリーに存在しない
    /// 相対パスでもフォルダー内なら読み込み要求を発火する。リンク経由の補完 (RelativeMarkdown) で
    /// 利用される正当な経路。仕様変更時はこのテストを更新する。
    /// </summary>
    [TestMethod]
    public async Task SelectAndLoadAsync_InsideFolderButNotInTree_StillFiresLoad()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);

        var notInTree = Path.Combine(AbsoluteRoot(), "phantom.md");
        var fired = 0;
        vm.PreviewLoadRequested += _ => fired++;

        await vm.SelectAndLoadAsync(notInTree);

        Assert.AreEqual(1, fired,
            "現挙動: ツリーに無くてもフォルダー境界内なら ReadAsync が走り PreviewLoadRequested が発火。");
        Assert.IsNull(vm.SelectedItem,
            "ツリーに無いノードは SelectedItem には設定されない。");
    }

    [TestMethod]
    public async Task SelectAndLoadAsync_WithoutOpenFolder_IsNoOp()
    {
        var vm = CreateViewModel();
        var fired = 0;
        vm.PreviewLoadRequested += _ => fired++;

        await vm.SelectAndLoadAsync(@"C:\anywhere\x.md");

        Assert.AreEqual(0, fired);
        Assert.AreEqual(0, _settings.SaveAsyncCalls);
    }

    // ----- FolderWatcher 連携 -----------------------------------------------

    [TestMethod]
    public async Task TreeMayHaveChanged_KeepsSelection_AndRebuildsTree()
    {
        Touch("README.md");
        Touch("notes.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        Assert.AreEqual(2, vm.MarkdownCount);
        var savesBefore = _settings.SaveAsyncCalls;

        Touch("extra.md");

        _watcher.RaiseTreeMayHaveChanged();
        await _settings.WaitForSaveCountAsync(savesBefore + 1);

        Assert.AreEqual(3, vm.MarkdownCount);
        Assert.IsNotNull(vm.SelectedItem);
        Assert.AreEqual("README.md", vm.SelectedItem!.Name);
    }

    [TestMethod]
    public async Task TreeMayHaveChanged_SelectionRemovedFromDisk_ClearsPreview()
    {
        Touch("README.md");
        Touch("notes.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        var notesFull = Path.Combine(AbsoluteRoot(), "notes.md");
        await vm.SelectAndLoadAsync(notesFull);
        var savesBefore = _settings.SaveAsyncCalls;

        var clearFired = 0;
        vm.PreviewClearRequested += () => clearFired++;

        File.Delete(notesFull);
        _watcher.RaiseTreeMayHaveChanged();
        await _settings.WaitForSaveCountAsync(savesBefore + 1);

        Assert.AreEqual(1, clearFired);
        Assert.IsNull(vm.SelectedItem);
        Assert.AreEqual(1, vm.MarkdownCount);
    }

    [TestMethod]
    public async Task FileContentChanged_Selected_ReloadsPreview()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        var readmeFull = Path.Combine(AbsoluteRoot(), "README.md");
        _reader.SetContent(readmeFull, "# first");

        var loads = new List<LoadRequest>();
        vm.PreviewLoadRequested += r => loads.Add(r);
        _reader.SetContent(readmeFull, "# updated");
        var savesBefore = _settings.SaveAsyncCalls;

        _watcher.RaiseFileContentChanged(readmeFull);
        await _settings.WaitForSaveCountAsync(savesBefore + 1);

        Assert.IsGreaterThanOrEqualTo(1, loads.Count);
        Assert.AreEqual("# updated", loads[^1].Markdown);
    }

    [TestMethod]
    public async Task FileContentChanged_NotSelected_DoesNothing()
    {
        Touch("README.md");
        Touch("notes.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        var savesBefore = _settings.SaveAsyncCalls;
        var notesFull = Path.Combine(AbsoluteRoot(), "notes.md");

        var loadFired = 0;
        vm.PreviewLoadRequested += _ => loadFired++;

        _watcher.RaiseFileContentChanged(notesFull);

        // 何も発火・保存しない。await の必要すらない (副作用ゼロ)。
        Assert.AreEqual(0, loadFired);
        Assert.AreEqual(savesBefore, _settings.SaveAsyncCalls);
    }

    // ----- コマンド --------------------------------------------------------

    [TestMethod]
    public async Task RevealInFileExplorerCommand_NoSelection_RevealsOpenedFolder()
    {
        // 空フォルダー → 選択なし → フォルダーを reveal する。
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        Assert.IsNull(vm.SelectedItem);

        vm.RevealInFileExplorerCommand.Execute(null);

        Assert.HasCount(1, _shell.RevealedPaths);
        Assert.AreEqual(AbsoluteRoot(), _shell.LastRevealedPath);
    }

    [TestMethod]
    public async Task RevealInFileExplorerCommand_WithSelection_RevealsFile()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        Assert.IsNotNull(vm.SelectedItem);

        vm.RevealInFileExplorerCommand.Execute(null);

        Assert.AreEqual(Path.Combine(AbsoluteRoot(), "README.md"), _shell.LastRevealedPath);
    }

    [TestMethod]
    public void RevealInFileExplorerCommand_NoFolderOpen_IsNoOp()
    {
        var vm = CreateViewModel();
        vm.RevealInFileExplorerCommand.Execute(null);
        Assert.IsEmpty(_shell.RevealedPaths);
    }

    [TestMethod]
    public async Task CopyFilePathCommand_WithSelection_CopiesAbsolutePath()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);

        vm.CopyFilePathCommand.Execute(null);

        Assert.AreEqual(Path.Combine(AbsoluteRoot(), "README.md"), _clipboard.LastWrite);
    }

    [TestMethod]
    public async Task CopyFilePathCommand_NoSelection_IsNoOp()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        Assert.IsNull(vm.SelectedItem);

        vm.CopyFilePathCommand.Execute(null);

        Assert.IsEmpty(_clipboard.Writes);
    }

    // ----- Dispose & EffectiveTheme ----------------------------------------

    [TestMethod]
    public async Task Dispose_DisposesWatcher_AndIgnoresLateEvents()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);
        var savesBefore = _settings.SaveAsyncCalls;

        vm.Dispose();

        Assert.AreEqual(1, _watcher.DisposeCalls);

        // Dispose 後にイベントを発火しても VM の副作用は起きない。
        var loads = 0;
        vm.PreviewLoadRequested += _ => loads++;
        _watcher.RaiseTreeMayHaveChanged();
        _watcher.RaiseFileContentChanged(Path.Combine(AbsoluteRoot(), "README.md"));

        await Task.Yield(); // give any scheduled continuation a chance
        Assert.AreEqual(savesBefore, _settings.SaveAsyncCalls);
        Assert.AreEqual(0, loads);
    }

    [TestMethod]
    public void Dispose_IdempotentCall_DoesNotDisposeWatcherTwice()
    {
        var vm = CreateViewModel();
        vm.Dispose();
        vm.Dispose();
        Assert.AreEqual(1, _watcher.DisposeCalls);
    }

    [TestMethod]
    public void EffectiveTheme_DelegatesToThemeProvider()
    {
        var vm = CreateViewModel();

        _settings.Current.Theme = AppTheme.Light;
        Assert.AreEqual("light", vm.EffectiveTheme());

        _settings.Current.Theme = AppTheme.Dark;
        Assert.AreEqual("dark", vm.EffectiveTheme());

        _settings.Current.Theme = AppTheme.System;
        _theme.System = AppTheme.Dark;
        Assert.AreEqual("dark", vm.EffectiveTheme());

        _theme.System = AppTheme.Light;
        Assert.AreEqual("light", vm.EffectiveTheme());
    }

    [TestMethod]
    public async Task PersistExpansionAsync_SavesCurrentExpansion()
    {
        Touch("docs/intro.md");
        Touch("docs/deep/leaf.md");
        var vm = CreateViewModel();
        await vm.OpenFolderAsync(_root);

        // ツリーを展開
        var docs = vm.RootItems.Single(r => r.Name == "docs");
        docs.IsExpanded = true;
        var deep = docs.Children.Single(c => c.Name == "deep");
        deep.IsExpanded = true;
        var savesBefore = _settings.SaveAsyncCalls;

        await vm.PersistExpansionAsync();

        Assert.AreEqual(savesBefore + 1, _settings.SaveAsyncCalls);
        var state = _settings.Current.GetOrCreateFolderState(AbsoluteRoot());
        CollectionAssert.AreEquivalent(new[] { "docs", "docs/deep" }, state.ExpandedFolders);
    }

    [TestMethod]
    public async Task PersistExpansionAsync_NoOpenFolder_DoesNothing()
    {
        var vm = CreateViewModel();
        await vm.PersistExpansionAsync();
        Assert.AreEqual(0, _settings.SaveAsyncCalls);
    }

    // ----- Custom テーマ ----------------------------------------------------

    [TestMethod]
    public void EffectiveTheme_Custom_FollowsResolvedDarkFlag()
    {
        _colorSchemeSource.Add(
            "darkish",
            """{"name":"Darkish","type":"dark","colors":{"editor.background":"#000000"}}""");
        _colorSchemeSource.Add(
            "lightish",
            """{"name":"Lightish","type":"light","colors":{"editor.background":"#ffffff"}}""");
        _colorSchemes.Reload();

        _settings.Current.Theme = AppTheme.Custom;
        _settings.Current.CustomThemeId = "darkish";
        var vm = CreateViewModel();
        Assert.AreEqual("dark", vm.EffectiveTheme());

        _settings.Current.CustomThemeId = "lightish";
        Assert.AreEqual("light", vm.EffectiveTheme());
    }

    [TestMethod]
    public void EffectiveTheme_CustomWithMissingId_FallsBackToSystemThemeProvider()
    {
        _colorSchemes.Reload();
        _settings.Current.Theme = AppTheme.Custom;
        _settings.Current.CustomThemeId = "ghost";
        _theme.System = AppTheme.Dark;
        var vm = CreateViewModel();

        Assert.AreEqual("dark", vm.EffectiveTheme());
    }

    [TestMethod]
    public void CurrentResolvedTheme_ReturnsNullForBuiltInThemes()
    {
        _colorSchemes.Reload();
        _settings.Current.Theme = AppTheme.Light;
        var vm = CreateViewModel();
        Assert.IsNull(vm.CurrentResolvedTheme());
    }

    [TestMethod]
    public void CurrentResolvedTheme_ReturnsResolvedForCustomTheme()
    {
        _colorSchemeSource.Add(
            "monokai",
            """{"name":"Monokai","type":"dark","colors":{"editor.background":"#272822"}}""");
        _colorSchemes.Reload();
        _settings.Current.Theme = AppTheme.Custom;
        _settings.Current.CustomThemeId = "monokai";
        var vm = CreateViewModel();

        var resolved = vm.CurrentResolvedTheme();
        Assert.IsNotNull(resolved);
        Assert.AreEqual("monokai", resolved.Id);
        Assert.AreEqual("#272822", resolved.CssVariables["--skim-bg"]);
    }

    // ----- OpenSingleFileAsync (single-file mode) -------------------------

    [TestMethod]
    public async Task OpenSingleFileAsync_SetsModeAndTitle()
    {
        Touch("README.md", "# r");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsTrue(vm.IsSingleFileMode);
        Assert.IsTrue(vm.HasFolder);
        Assert.IsTrue(vm.HasAnyMarkdown);
        Assert.AreEqual(1, vm.MarkdownCount);
        Assert.AreEqual("README.md", vm.OpenedFolderName);
        Assert.AreEqual("README.md \u2014 SkimDown", vm.WindowTitle);
        Assert.AreEqual(AbsoluteRoot(), vm.OpenedFolderPath);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_DoesNotUpdateRecentFolders()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsEmpty(_settings.Current.RecentFolders);
        Assert.IsEmpty(vm.RecentFolders);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_DoesNotUpdateLastFolderPath()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsNull(_settings.Current.LastFolderPath);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_DoesNotCreateFolderState()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsEmpty(_settings.Current.FolderStates);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_DoesNotPersist_NoSaveAsyncCalls()
    {
        Touch("README.md");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.AreEqual(0, _settings.SaveAsyncCalls,
            "Single-file mode は永続化を一切呼ばないことが上流仕様。");
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_LeavesRootItemsEmpty()
    {
        Touch("README.md");
        Touch("other.md");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsEmpty(vm.RootItems);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_SetsSelectedItem_AndFiresPreview()
    {
        Touch("README.md", "# r");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");
        _reader.SetContent(filePath, "# single-file-body");

        LoadRequest? captured = null;
        vm.PreviewLoadRequested += r => captured = r;

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsNotNull(vm.SelectedItem);
        Assert.AreEqual("README.md", vm.SelectedItem!.Name);
        Assert.AreEqual(filePath, vm.SelectedItem.FullPath, ignoreCase: true);
        Assert.IsNotNull(captured);
        Assert.AreEqual("# single-file-body", captured!.Markdown);
        Assert.AreEqual("README.md", captured.RelativePath);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_StartsWatcher_OnParentFolder()
    {
        Touch("nested/inner.md");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "nested", "inner.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.AreEqual(1, _watcher.WatchCalls);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(AbsoluteRoot(), "nested")).TrimEnd(Path.DirectorySeparatorChar),
            _watcher.LastWatchedPath,
            ignoreCase: true);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_IsNoOp_ForNonMarkdownFile()
    {
        Touch("notes.txt");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "notes.txt");

        var fired = 0;
        vm.PreviewLoadRequested += _ => fired++;

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsFalse(vm.IsSingleFileMode);
        Assert.IsFalse(vm.HasFolder);
        Assert.AreEqual(0, fired);
        Assert.AreEqual(0, _watcher.WatchCalls);
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_IsNoOp_ForMissingFile()
    {
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "ghost.md");

        await vm.OpenSingleFileAsync(filePath);

        Assert.IsFalse(vm.IsSingleFileMode);
        Assert.IsFalse(vm.HasFolder);
        Assert.AreEqual(0, _watcher.WatchCalls);
    }

    [TestMethod]
    public async Task OpenFolderAsync_AfterSingleFile_ResetsIsSingleFileMode()
    {
        Touch("README.md", "# r");
        Touch("docs/intro.md");
        var vm = CreateViewModel();

        await vm.OpenSingleFileAsync(Path.Combine(AbsoluteRoot(), "README.md"));
        Assert.IsTrue(vm.IsSingleFileMode);

        await vm.OpenFolderAsync(_root);

        Assert.IsFalse(vm.IsSingleFileMode);
        Assert.IsNotEmpty(vm.RootItems);
        // 通常 folder mode の永続化が走る (FolderState 作成 + RecentFolders 追加)。
        Assert.IsNotEmpty(_settings.Current.RecentFolders);
        Assert.AreEqual(AbsoluteRoot(), _settings.Current.LastFolderPath);
    }

    [TestMethod]
    public async Task OnTreeMayHaveChanged_InSingleFileMode_DoesNotPersist()
    {
        Touch("README.md", "# r");
        var vm = CreateViewModel();
        await vm.OpenSingleFileAsync(Path.Combine(AbsoluteRoot(), "README.md"));
        var savesBefore = _settings.SaveAsyncCalls;

        // sibling ファイルを増やしても single-file mode ではツリー再走査が走らないことを
        // SaveAsync 回数が増えないことで確認する。
        Touch("sibling.md");
        _watcher.RaiseTreeMayHaveChanged();
        await Task.Yield();

        Assert.AreEqual(savesBefore, _settings.SaveAsyncCalls,
            "Single-file mode 中の tree change は no-op で SaveAsync を呼ばない。");
    }

    [TestMethod]
    public async Task OnFileContentChanged_InSingleFileMode_ReloadsMatchingFile()
    {
        Touch("README.md", "# r");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");
        _reader.SetContent(filePath, "# initial");
        await vm.OpenSingleFileAsync(filePath);

        // 初回 load の分は受け取り済みなので、reload による発火だけを観察する。
        var captured = new List<LoadRequest>();
        vm.PreviewLoadRequested += r => captured.Add(r);
        _reader.SetContent(filePath, "# updated");

        _watcher.RaiseFileContentChanged(filePath);
        await Task.Yield();
        // ReloadSingleFileAsync は ReadAsync → PreviewLoadRequested の chain なので追加 yield。
        await Task.Yield();

        Assert.AreEqual(1, captured.Count);
        Assert.AreEqual("# updated", captured[0].Markdown);
    }

    [TestMethod]
    public async Task OnFileContentChanged_InSingleFileMode_IgnoresOtherFile()
    {
        Touch("README.md", "# r");
        Touch("other.md", "# o");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");
        await vm.OpenSingleFileAsync(filePath);

        var fired = 0;
        vm.PreviewLoadRequested += _ => fired++;

        _watcher.RaiseFileContentChanged(Path.Combine(AbsoluteRoot(), "other.md"));
        await Task.Yield();

        Assert.AreEqual(0, fired);
    }

    [TestMethod]
    public async Task OnFileContentChanged_InSingleFileMode_DoesNotPersist()
    {
        Touch("README.md", "# r");
        var vm = CreateViewModel();
        var filePath = Path.Combine(AbsoluteRoot(), "README.md");
        await vm.OpenSingleFileAsync(filePath);
        var savesBefore = _settings.SaveAsyncCalls;
        _reader.SetContent(filePath, "# updated");

        _watcher.RaiseFileContentChanged(filePath);
        await Task.Yield();
        await Task.Yield();

        Assert.AreEqual(savesBefore, _settings.SaveAsyncCalls,
            "Single-file mode の reload は永続化を起こさない。");
    }

    [TestMethod]
    public async Task OpenSingleFileAsync_DoesNotTouchSidebarVisibleSetting()
    {
        Touch("README.md");
        // 事前に persisted SidebarVisible = true を設定する。
        _settings.Current.SidebarVisible = true;
        var vm = CreateViewModel();

        await vm.OpenSingleFileAsync(Path.Combine(AbsoluteRoot(), "README.md"));

        // Single-file mode は visual override で hide するが、persisted 設定は触らない。
        Assert.IsTrue(_settings.Current.SidebarVisible,
            "永続 SidebarVisible は folder mode 用の真実として保持される。");
    }
}
