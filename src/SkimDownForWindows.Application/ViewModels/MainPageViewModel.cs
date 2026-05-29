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
        _picker = picker;
        LinkResolver = linkResolver;

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
        _singleFilePath = null;

        OpenedFolderPath = canonical;
        OpenedFolderName = Path.GetFileName(canonical) ?? canonical;
        HasFolder = true;
        WindowTitle = $"{OpenedFolderName} — SkimDown";

        var files = _scanner.Scan(canonical);
        var root = _treeBuilder.Build(canonical, files);
        // 別フォルダーへ切り替える場合、古い folder の MarkdownTreeItem 参照が
        // 新しい RootItems と無関係な孤立インスタンスになる。先に SelectedItem を
        // null にしておくことで、TreeView 側の選択同期が古い参照を相手取らずに済む。
        SelectedItem = null;
        ReplaceRoot(root);
        MarkdownCount = root.MarkdownCount;
        HasAnyMarkdown = MarkdownCount > 0;

        var state = _settings.Current.GetOrCreateFolderState(canonical);
        ApplyExpansionState(state.ExpandedFolders);

        var pick = _picker.Pick(root, state.LastSelectedRelativePath);
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

        var expansion = CollectExpandedFolders();
        var currentSelectionRel = SelectedItem?.RelativePath;

        var files = _scanner.Scan(OpenedFolderPath);
        var root = _treeBuilder.Build(OpenedFolderPath, files);
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
        state.ExpandedFolders = CollectExpandedFolders();
        await _settings.SaveAsync();
    }

    public async Task PersistExpansionAsync()
    {
        if (string.IsNullOrEmpty(OpenedFolderPath)) return;
        var state = _settings.Current.GetOrCreateFolderState(OpenedFolderPath);
        state.ExpandedFolders = CollectExpandedFolders();
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
