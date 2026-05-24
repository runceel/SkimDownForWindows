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
    private readonly MarkdownScanner _scanner;
    private readonly MarkdownTreeBuilder _treeBuilder;
    private readonly InitialSelectionPicker _picker;

    private bool _disposed;

    /// <summary>設定リポジトリへの直接アクセス (XAML バインドやコードビハインドから利用)。</summary>
    public ISettingsRepository Settings => _settings;

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
    /// </summary>
    public string EffectiveTheme() => _themeProvider.Resolve(_settings.Current.Theme);

    public async Task OpenFolderAsync(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !_fileSystem.DirectoryExists(folderPath))
        {
            return;
        }

        var canonical = PathHelpers.Canonicalize(folderPath);

        OpenedFolderPath = canonical;
        OpenedFolderName = Path.GetFileName(canonical) ?? canonical;
        HasFolder = true;
        WindowTitle = $"{OpenedFolderName} — SkimDown";

        var files = _scanner.Scan(canonical);
        var root = _treeBuilder.Build(canonical, files);
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
        SelectedItem = item;
        ExpandAncestors(item);

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
        if (SelectedItem is null) return;
        if (string.Equals(SelectedItem.FullPath, absolutePath, StringComparison.OrdinalIgnoreCase))
        {
            _ = SelectAndLoadAsync(absolutePath);
        }
    }

    private async void OnTreeMayHaveChanged()
    {
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
                SelectedItem = match;
                ExpandAncestors(match);
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
