using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Theme;
using SkimDownForWindows.Application.Utilities;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.ViewModels;

/// <summary>
/// 単一ウィンドウの SkimDown アプリの coordinator。
/// 設定・スキャナー・ツリービルダー・ファイル監視・現在選択を統括する。
///
/// すべての外部 I/O は <see cref="Abstractions"/> 配下のインターフェース経由。
/// <see cref="IDisposable.Dispose"/> でウォッチャーの購読解除と破棄を行うため、
/// ウィンドウスコープで Scoped 登録すること。
/// </summary>
public partial class MainPageViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsRepository _settings;
    private readonly IFolderWatcher _watcher;
    private readonly IMarkdownFileReader _markdownReader;
    private readonly IFileSystem _fileSystem;
    private readonly IShellService _shellService;
    private readonly IClipboardService _clipboardService;
    private readonly ISystemThemeProvider _themeProvider;
    private readonly ColorSchemeRegistry _colorSchemes;
    private readonly MarkdownScanner _scanner;
    private readonly MarkdownTreeBuilder _treeBuilder;
    private readonly RecentMarkdownListBuilder _recentListBuilder;
    private readonly InitialSelectionPicker _picker;

    private bool _disposed;

    /// <summary>
    /// Single-file mode で表示している絶対ファイルパス。folder mode 中は <c>null</c>。
    /// <see cref="OpenedFolderPath"/> はリソース解決のため親フォルダーを保持するが、
    /// 「実際に開いているファイル」を識別するためにこれを別途持つ。
    /// </summary>
    private string? _singleFilePath;

    /// <summary>設定リポジトリへの直接アクセス (XAML バインドやコードビハインドから利用)。</summary>
    public ISettingsRepository Settings => _settings;

    /// <summary>登録カスタムテーマ一覧 (UI 構築用)。</summary>
    public ColorSchemeRegistry ColorSchemes => _colorSchemes;

    /// <summary>リンク分類器。プレビューからのリンクイベント解決に使う。</summary>
    public LinkResolver LinkResolver { get; }

    public ObservableCollection<MarkdownTreeItem> RootItems { get; } = new();

    public ObservableCollection<RecentFolderEntry> RecentFolders { get; } = new();

    [ObservableProperty]
    public partial string? OpenedFolderPath { get; set; }

    [ObservableProperty]
    public partial string OpenedFolderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MarkdownCount { get; set; }

    [ObservableProperty]
    public partial bool HasFolder { get; set; }

    [ObservableProperty]
    public partial bool HasAnyMarkdown { get; set; }

    [ObservableProperty]
    public partial MarkdownTreeItem? SelectedItem { get; set; }

    /// <summary>
    /// サイドバー (ファイル一覧) の表示モード。<see cref="SidebarViewMode.Tree"/> はフォルダー階層ツリー、
    /// <see cref="SidebarViewMode.RecentlyModified"/> は更新日順のフラット一覧。
    /// 初期値はコンストラクターで <see cref="AppSettings.SidebarViewMode"/> から復元する。
    /// 変更は <see cref="SetSidebarViewModeAsync"/> 経由で行い、設定の永続化と再構築を伴う。
    /// </summary>
    [ObservableProperty]
    public partial SidebarViewMode SidebarViewMode { get; set; }

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "SkimDown";

    /// <summary>
    /// Single-file mode (1 個の Markdown ファイルだけを表示する mode) であるかどうか。
    /// 上流 macOS 版の <c>isSingleFileMode</c> 相当。Explorer ダブルクリック / CLI <c>.md</c> 引数 /
    /// ファイル drag-drop で <c>true</c> になり、<see cref="OpenFolderAsync"/> で <c>false</c> に戻る。
    ///
    /// この mode 中は:
    /// <list type="bullet">
    ///   <item>サイドバー (ツリー) は強制的に非表示</item>
    ///   <item><see cref="RootItems"/> は空</item>
    ///   <item><see cref="ISettingsRepository"/> の RecentFolders / LastFolderPath / FolderStates は更新されない</item>
    ///   <item>SidebarVisible 永続設定は触らない (= folder mode 用の真実として保持)</item>
    /// </list>
    /// </summary>
    [ObservableProperty]
    public partial bool IsSingleFileMode { get; set; }

    /// <summary>
    /// 「single-file 起動を親フォルダー表示に変換する」設定が有効な時に、
    /// 初期表示としてサイドバーを一時的に折り畳むための runtime フラグ。
    /// 永続設定 <see cref="AppSettings.SidebarVisible"/> とは独立して扱う。
    /// </summary>
    [ObservableProperty]
    public partial bool IsSidebarTemporarilyCollapsed { get; set; }

    /// <summary>新しい Markdown 内容をプレビューに反映させたい時に発火する。</summary>
    public event Action<LoadRequest>? PreviewLoadRequested;

    /// <summary>プレビューを empty 状態にクリアさせたい時に発火する。</summary>
    public event Action? PreviewClearRequested;

    public MainPageViewModel(
        ISettingsRepository settings,
        IFolderWatcher watcher,
        IMarkdownFileReader markdownReader,
        IFileSystem fileSystem,
        IShellService shellService,
        IClipboardService clipboardService,
        ISystemThemeProvider themeProvider,
        ColorSchemeRegistry colorSchemes,
        MarkdownScanner scanner,
        MarkdownTreeBuilder treeBuilder,
        RecentMarkdownListBuilder recentListBuilder,
        InitialSelectionPicker picker,
        LinkResolver linkResolver)
    {
        _settings = settings;
        _watcher = watcher;
        _markdownReader = markdownReader;
        _fileSystem = fileSystem;
        _shellService = shellService;
        _clipboardService = clipboardService;
        _themeProvider = themeProvider;
        _colorSchemes = colorSchemes;
        _scanner = scanner;
        _treeBuilder = treeBuilder;
        _recentListBuilder = recentListBuilder;
        _picker = picker;
        LinkResolver = linkResolver;

        SidebarViewMode = settings.Current.SidebarViewMode;

        _watcher.TreeMayHaveChanged += OnTreeMayHaveChanged;
        _watcher.FileContentChanged += OnFileContentChanged;

        RebuildRecentFolders();
    }

    /// <summary>
    /// 現在のテーマ設定を実効値 (<c>"light"</c> または <c>"dark"</c>) に解決する。
    ///
    /// カスタムテーマの場合は <see cref="ColorSchemeRegistry"/> の解決結果に従い isDark 判定する。
    /// カスタムテーマが見つからない場合は <see cref="ISystemThemeProvider"/> 経由で OS 設定に従う。
    /// </summary>
    public string EffectiveTheme()
    {
        var current = _settings.Current;
        if (current.Theme == AppTheme.Custom)
        {
            var resolved = _colorSchemes.Resolve(current.CustomThemeId);
            if (resolved is not null)
            {
                return resolved.IsDark ? "dark" : "light";
            }
            // カスタムテーマが消えている場合は System 扱い。
            return _themeProvider.Resolve(AppTheme.System);
        }
        return _themeProvider.Resolve(current.Theme);
    }

    /// <summary>
    /// 現在のテーマ選択を <see cref="ThemeSelection"/> として返す。
    /// </summary>
    public ThemeSelection CurrentThemeSelection()
        => new(_settings.Current.Theme, _settings.Current.CustomThemeId);

    /// <summary>
    /// 現在のテーマがカスタムテーマで解決済みなら、対応する <see cref="ResolvedTheme"/> を返す。
    /// それ以外は <c>null</c>。
    /// </summary>
    public ResolvedTheme? CurrentResolvedTheme()
    {
        var current = _settings.Current;
        if (current.Theme != AppTheme.Custom)
        {
            return null;
        }
        return _colorSchemes.Resolve(current.CustomThemeId);
    }

    public async Task OpenFolderAsync(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !_fileSystem.DirectoryExists(folderPath))
        {
            return;
        }

        var canonical = PathHelpers.Canonicalize(folderPath);

        // Single-file mode から folder mode への切り替え。code-behind 側で
        // IsSingleFileMode の変化を見てサイドバー visual state を復元する。
        IsSingleFileMode = false;
        IsSidebarTemporarilyCollapsed = false;
        _singleFilePath = null;

        OpenedFolderPath = canonical;
        OpenedFolderName = Path.GetFileName(canonical) ?? canonical;
        HasFolder = true;
        WindowTitle = $"{OpenedFolderName} — SkimDown";

        var root = BuildRoot(canonical);
        // 別フォルダーへ切り替える場合、古い folder の MarkdownTreeItem 参照が
        // 新しい RootItems と無関係な孤立インスタンスになる。先に SelectedItem を
        // null にしておくことで、TreeView 側の選択同期が古い参照を相手取らずに済む。
        SelectedItem = null;
        ReplaceRoot(root);
        MarkdownCount = root.MarkdownCount;
        HasAnyMarkdown = MarkdownCount > 0;

        var state = _settings.Current.GetOrCreateFolderState(canonical);
        ApplyExpansionState(state.ExpandedFolders);

        var pick = PickInitialSelection(root, state.LastSelectedRelativePath);
        if (pick is not null && HasAnyMarkdown)
        {
            await SelectAndLoadAsync(pick);
        }
        else
        {
            SelectedItem = null;
            PreviewClearRequested?.Invoke();
        }

        _settings.Current.UpdateRecentFolders(canonical);
        await _settings.SaveAsync();
        RebuildRecentFolders();

        _watcher.Watch(canonical);
    }

    /// <summary>
    /// 指定 Markdown ファイル 1 件だけを表示する single-file mode に入る。
    /// 上流 macOS 版の <c>DocumentWindowController.openFile</c> 相当の挙動。
    ///
    /// 設定リポジトリの RecentFolders / LastFolderPath / FolderStates / SidebarVisible は
    /// **一切更新しない**。サイドバーの visual な hide は呼び出し元 (Presentation 層) が
    /// <see cref="IsSingleFileMode"/> プロパティを購読して行う。
    ///
    /// 親フォルダーを <see cref="IFolderWatcher"/> で監視するため、外部編集で対象ファイルが
    /// 変更されると <see cref="OnFileContentChanged"/> 経由で <see cref="ReloadSingleFileAsync"/>
    /// が走り preview が更新される。
    /// </summary>
    public async Task OpenSingleFileAsync(string filePath)
    {
        if (_settings.Current.OpenContainingFolderOnSingleFileActivation)
        {
            var openedAsFolder = await TryOpenSingleFileAsContainingFolderAsync(filePath);
            if (openedAsFolder)
            {
                return;
            }
        }

        await OpenSingleFileLightweightAsync(filePath);
    }

    private async Task OpenSingleFileLightweightAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !_fileSystem.FileExists(filePath))
        {
            return;
        }
        if (!PathHelpers.IsMarkdownFile(filePath))
        {
            return;
        }

        var canonicalFile = PathHelpers.Canonicalize(filePath);
        var parent = Path.GetDirectoryName(canonicalFile);
        if (string.IsNullOrEmpty(parent) || !_fileSystem.DirectoryExists(parent))
        {
            return;
        }
        var canonicalParent = PathHelpers.Canonicalize(parent);

        IsSingleFileMode = true;
        IsSidebarTemporarilyCollapsed = false;
        _singleFilePath = canonicalFile;

        // OpenedFolderPath はリソース解決 (相対 link や画像) のための base としてだけ使う。
        // ナビゲーションのため (= folder mode のツリー root) ではない点に注意。
        OpenedFolderPath = canonicalParent;
        var fileName = Path.GetFileName(canonicalFile);
        OpenedFolderName = fileName;
        HasFolder = true;
        WindowTitle = $"{fileName} \u2014 SkimDown";

        // ツリーは空のまま (サイドバーは hidden)。empty-state overlay を出さないため
        // HasAnyMarkdown / MarkdownCount を 1 にセット。
        RootItems.Clear();
        MarkdownCount = 1;
        HasAnyMarkdown = true;

        // synthetic な MarkdownTreeItem を SelectedItem に割り当てる。
        // RootItems には入れない (= サイドバー hidden + tree empty を保つ)。
        var rel = PathHelpers.RelativeFromRoot(canonicalParent, canonicalFile);
        var syntheticItem = new MarkdownTreeItem(fileName, canonicalFile, rel, isFolder: false);
        SelectedItem = syntheticItem;

        // dedicated reload を走らせる。SelectAndLoadAsync は folder mode 用なので使わない。
        await ReloadSingleFileAsync();

        _watcher.Watch(canonicalParent);
    }

    private async Task<bool> TryOpenSingleFileAsContainingFolderAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !_fileSystem.FileExists(filePath))
        {
            return false;
        }
        if (!PathHelpers.IsMarkdownFile(filePath))
        {
            return false;
        }

        var canonicalFile = PathHelpers.Canonicalize(filePath);
        var parent = Path.GetDirectoryName(canonicalFile);
        if (string.IsNullOrEmpty(parent) || !_fileSystem.DirectoryExists(parent))
        {
            return false;
        }

        await OpenFolderAsync(parent);
        await SelectAndLoadAsync(canonicalFile);
        IsSidebarTemporarilyCollapsed = true;
        return true;
    }

    /// <summary>
    /// Single-file mode 中の対象ファイルを再読込し、preview に流し直す。
    /// 設定の保存・選択状態の永続化は一切行わない。
    /// </summary>
    private async Task ReloadSingleFileAsync()
    {
        if (_singleFilePath is null || OpenedFolderPath is null) return;
        if (!_fileSystem.FileExists(_singleFilePath)) return;

        var text = await _markdownReader.ReadAsync(_singleFilePath);
        var rel = PathHelpers.RelativeFromRoot(OpenedFolderPath, _singleFilePath);
        PreviewLoadRequested?.Invoke(new LoadRequest(text, rel, EffectiveTheme()));
    }

    private void ReplaceRoot(MarkdownTreeItem root)
    {
        RootItems.Clear();
        foreach (var child in root.Children)
        {
            RootItems.Add(child);
        }
    }

    private bool RootMatches(MarkdownTreeItem root)
        => TreeItemCollectionsEqual(RootItems, root.Children);

    private static bool TreeItemCollectionsEqual(IList<MarkdownTreeItem> current, IList<MarkdownTreeItem> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (!TreeItemsEqual(current[i], next[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TreeItemsEqual(MarkdownTreeItem current, MarkdownTreeItem next)
        => current.IsFolder == next.IsFolder
           && current.MarkdownCount == next.MarkdownCount
           && current.LastModified == next.LastModified
           && string.Equals(current.Name, next.Name, StringComparison.Ordinal)
           && string.Equals(current.FullPath, next.FullPath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(current.RelativePath, next.RelativePath, StringComparison.Ordinal)
           && string.Equals(current.RelativeFolder, next.RelativeFolder, StringComparison.Ordinal)
           && TreeItemCollectionsEqual(current.Children, next.Children);

    private void ApplyExpansionState(IEnumerable<string> expandedRelativePaths)
    {
        var set = new HashSet<string>(expandedRelativePaths, StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<MarkdownTreeItem> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    if (set.Contains(n.RelativePath))
                    {
                        n.IsExpanded = true;
                    }
                    Walk(n.Children);
                }
            }
        }
        Walk(RootItems);
    }

    public List<string> CollectExpandedFolders()
    {
        var list = new List<string>();
        void Walk(IEnumerable<MarkdownTreeItem> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    if (n.IsExpanded) list.Add(n.RelativePath);
                    Walk(n.Children);
                }
            }
        }
        Walk(RootItems);
        return list;
    }

    public async Task SelectAndLoadAsync(string absoluteFilePath)
    {
        if (string.IsNullOrEmpty(OpenedFolderPath)) return;
        if (!PathHelpers.IsInsideFolder(OpenedFolderPath, absoluteFilePath)) return;

        var rel = PathHelpers.RelativeFromRoot(OpenedFolderPath, absoluteFilePath);
        var item = FindFileItemByRelativePath(rel);
        // 親フォルダーを先に展開してから SelectedItem を通知する。
        // こうすることで PropertyChanged を受けて TreeView 側に選択を反映する
        // code-behind 側で対象 TreeViewItem の Container が存在する状態になる。
        ExpandAncestors(item);
        SelectedItem = item;

        var text = await _markdownReader.ReadAsync(absoluteFilePath);

        PreviewLoadRequested?.Invoke(new LoadRequest(text, rel, EffectiveTheme()));

        var state = _settings.Current.GetOrCreateFolderState(OpenedFolderPath);
        state.LastSelectedRelativePath = rel;
        await _settings.SaveAsync();
    }

    private MarkdownTreeItem? FindFileItemByRelativePath(string rel)
    {
        MarkdownTreeItem? hit = null;
        void Walk(IEnumerable<MarkdownTreeItem> nodes)
        {
            if (hit is not null) return;
            foreach (var n in nodes)
            {
                if (hit is not null) return;
                if (!n.IsFolder && string.Equals(n.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
                {
                    hit = n;
                    return;
                }
                if (n.IsFolder)
                {
                    Walk(n.Children);
                }
            }
        }
        Walk(RootItems);
        return hit;
    }

    private void ExpandAncestors(MarkdownTreeItem? item)
    {
        if (item is null) return;
        var rel = item.RelativePath;
        if (string.IsNullOrEmpty(rel)) return;

        var parts = rel.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var prefix = string.Join('/', parts, 0, i);
            ExpandFolderByRelativePath(prefix);
        }
    }

    private void ExpandFolderByRelativePath(string rel)
    {
        void Walk(IEnumerable<MarkdownTreeItem> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    if (string.Equals(n.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
                    {
                        n.IsExpanded = true;
                    }
                    Walk(n.Children);
                }
            }
        }
        Walk(RootItems);
    }

    private void OnFileContentChanged(string absolutePath)
    {
        if (IsSingleFileMode)
        {
            if (_singleFilePath is not null &&
                string.Equals(_singleFilePath, absolutePath, StringComparison.OrdinalIgnoreCase))
            {
                _ = ReloadSingleFileAsync();
            }
            return;
        }

        if (SelectedItem is null) return;
        if (string.Equals(SelectedItem.FullPath, absolutePath, StringComparison.OrdinalIgnoreCase))
        {
            _ = SelectAndLoadAsync(absolutePath);
        }
    }

    private async void OnTreeMayHaveChanged()
    {
        // Single-file mode ではツリーを使わないので再走査も不要。
        // 親フォルダー上の add/delete/rename は無視し、対象ファイルの content 変更だけ
        // OnFileContentChanged 側で reload させる。
        if (IsSingleFileMode) return;

        if (string.IsNullOrEmpty(OpenedFolderPath)) return;

        var root = BuildRoot(OpenedFolderPath);
        if (RootMatches(root))
        {
            return;
        }

        var expansion = CollectExpandedFolders();
        var currentSelectionRel = SelectedItem?.RelativePath;

        ReplaceRoot(root);
        MarkdownCount = root.MarkdownCount;
        HasAnyMarkdown = MarkdownCount > 0;
        ApplyExpansionState(expansion);

        if (!string.IsNullOrEmpty(currentSelectionRel))
        {
            var match = FindFileItemByRelativePath(currentSelectionRel);
            if (match is not null)
            {
                // SelectAndLoadAsync と同じ順序: 親を先に展開してから選択を通知する。
                ExpandAncestors(match);
                SelectedItem = match;
            }
            else
            {
                SelectedItem = null;
                PreviewClearRequested?.Invoke();
            }
        }

        var state = _settings.Current.GetOrCreateFolderState(OpenedFolderPath);
        // 更新日順モードでは展開状態が無い (CollectExpandedFolders は空を返す) ため、
        // ここで保存すると Tree モードで保存済みの展開状態を消してしまう。Tree モードのときだけ保存する。
        if (SidebarViewMode == SidebarViewMode.Tree)
        {
            state.ExpandedFolders = CollectExpandedFolders();
        }
        await _settings.SaveAsync();
    }

    public async Task PersistExpansionAsync()
    {
        if (string.IsNullOrEmpty(OpenedFolderPath)) return;
        // Tree モードのときだけ展開状態を保存する (更新日順モードでは空で上書きしない)。
        if (SidebarViewMode != SidebarViewMode.Tree) return;
        var state = _settings.Current.GetOrCreateFolderState(OpenedFolderPath);
        state.ExpandedFolders = CollectExpandedFolders();
        await _settings.SaveAsync();
    }

    /// <summary>
    /// 現在の <see cref="SidebarViewMode"/> に応じて、フォルダーをスキャンしてツリー root を構築する。
    /// Tree なら <see cref="MarkdownTreeBuilder"/>、RecentlyModified なら
    /// <see cref="RecentMarkdownListBuilder"/> を使う。どちらも Children と MarkdownCount を持つ root を返す。
    /// </summary>
    private MarkdownTreeItem BuildRoot(string folder)
    {
        var files = _scanner.Scan(folder);
        return SidebarViewMode == SidebarViewMode.RecentlyModified
            ? _recentListBuilder.Build(folder, files)
            : _treeBuilder.Build(folder, files);
    }

    /// <summary>
    /// フォルダーを開いた直後の初期選択を、モードに応じて決める。
    /// Tree は <see cref="InitialSelectionPicker"/> (前回 → README → 先頭)。
    /// RecentlyModified は README を優先せず「前回 → 先頭 (= 最新)」とする。
    /// </summary>
    private string? PickInitialSelection(MarkdownTreeItem root, string? lastSelectedRelativePath)
    {
        if (SidebarViewMode != SidebarViewMode.RecentlyModified)
        {
            return _picker.Pick(root, lastSelectedRelativePath);
        }

        if (!string.IsNullOrEmpty(lastSelectedRelativePath))
        {
            var match = root.Children.FirstOrDefault(c =>
                !c.IsFolder
                && string.Equals(c.RelativePath, lastSelectedRelativePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.FullPath;
            }
        }

        return root.Children.FirstOrDefault(c => !c.IsFolder)?.FullPath;
    }

    /// <summary>
    /// サイドバーの表示モードを切り替える。設定を永続化し、folder mode 中なら一覧を作り直して
    /// 現在選択中のファイルを (相対パスで) 引き継ぐ。single-file mode では設定だけ更新する。
    ///
    /// 再構築 (<see cref="BuildRoot"/> + <see cref="ReplaceRoot"/>) は await を挟まず同期的に適用するため、
    /// watcher 由来の <see cref="OnTreeMayHaveChanged"/> と競合しても古い結果で上書きされない
    /// (各経路が最新のモード/フォルダーで原子的に適用される)。
    /// </summary>
    [RelayCommand]
    private async Task SetSidebarViewModeAsync(SidebarViewMode mode)
    {
        if (SidebarViewMode == mode)
        {
            return;
        }

        // Tree から離れる場合、現在の展開状態を退避しておき、後で Tree に戻したときに復元できるようにする。
        if (SidebarViewMode == SidebarViewMode.Tree
            && !IsSingleFileMode
            && !string.IsNullOrEmpty(OpenedFolderPath))
        {
            _settings.Current.GetOrCreateFolderState(OpenedFolderPath).ExpandedFolders = CollectExpandedFolders();
        }

        SidebarViewMode = mode;
        _settings.Current.SidebarViewMode = mode;

        if (!IsSingleFileMode && HasFolder && !string.IsNullOrEmpty(OpenedFolderPath))
        {
            var selectionRel = SelectedItem?.RelativePath;

            var root = BuildRoot(OpenedFolderPath);
            ReplaceRoot(root);
            MarkdownCount = root.MarkdownCount;
            HasAnyMarkdown = MarkdownCount > 0;

            if (mode == SidebarViewMode.Tree)
            {
                ApplyExpansionState(_settings.Current.GetOrCreateFolderState(OpenedFolderPath).ExpandedFolders);
            }

            if (!string.IsNullOrEmpty(selectionRel))
            {
                var match = FindFileItemByRelativePath(selectionRel);
                // モード切替では対象ファイル集合は不変なので通常 match は見つかる。
                // 新しいインスタンスへ選択を貼り替えることで、code-behind の視覚的選択同期が機能する。
                ExpandAncestors(match);
                SelectedItem = match;
            }
        }

        await _settings.SaveAsync();
    }

    private void RebuildRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var p in _settings.Current.RecentFolders)
        {
            RecentFolders.Add(new RecentFolderEntry(p, Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))));
        }
    }

    [RelayCommand]
    private void RevealInFileExplorer()
    {
        var path = SelectedItem?.FullPath ?? OpenedFolderPath;
        if (string.IsNullOrEmpty(path)) return;
        _shellService.Reveal(path);
    }

    [RelayCommand]
    private void CopyFilePath()
    {
        var path = SelectedItem?.FullPath;
        if (string.IsNullOrEmpty(path)) return;
        _clipboardService.SetText(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher.TreeMayHaveChanged -= OnTreeMayHaveChanged;
        _watcher.FileContentChanged -= OnFileContentChanged;
        _watcher.Dispose();
    }
}
