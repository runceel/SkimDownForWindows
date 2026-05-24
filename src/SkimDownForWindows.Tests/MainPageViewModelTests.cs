using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Application.Models;
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
}
