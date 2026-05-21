using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkimDownForWindows.Core;
using SkimDownForWindows.Markdown;
using SkimDownForWindows.Models;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.ViewModels;

/// <summary>
/// Coordinates the single-window SkimDown app:
/// settings, scanner/tree builder, file watcher, current selection.
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    public SettingsStore Settings { get; }
    private readonly MarkdownScanner _scanner = new();
    private readonly MarkdownTreeBuilder _treeBuilder = new();
    private readonly InitialSelectionPicker _picker = new();
    public LinkResolver LinkResolver { get; } = new();
    public FolderWatcher? Watcher { get; private set; }

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

    /// <summary>Raised when the page should display new Markdown content.</summary>
    public event Action<LoadRequest>? PreviewLoadRequested;

    /// <summary>Raised when the page should clear the preview (empty state).</summary>
    public event Action? PreviewClearRequested;

    public MainPageViewModel(SettingsStore settings, FolderWatcher? watcher)
    {
        Settings = settings;
        Watcher = watcher;
        if (watcher is not null)
        {
            watcher.TreeMayHaveChanged += OnTreeMayHaveChanged;
            watcher.FileContentChanged += OnFileContentChanged;
        }
        RebuildRecentFolders();
    }

    public string EffectiveTheme()
    {
        switch (Settings.Current.Theme)
        {
            case AppTheme.Light: return "light";
            case AppTheme.Dark: return "dark";
            default:
                try
                {
                    var ui = new Windows.UI.ViewManagement.UISettings();
                    var bg = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                    return (bg.R + bg.G + bg.B) < 384 ? "dark" : "light";
                }
                catch { return "light"; }
        }
    }

    public async Task OpenFolderAsync(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
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

        var state = Settings.GetOrCreateFolderState(canonical);
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

        Settings.UpdateRecentFolders(canonical);
        await Settings.SaveAsync();
        RebuildRecentFolders();

        Watcher?.Watch(canonical);
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

        string text;
        try
        {
            text = await File.ReadAllTextAsync(absoluteFilePath, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            text = $"# Read error\n\n```\n{ex.Message}\n```";
        }

        PreviewLoadRequested?.Invoke(new LoadRequest(text, rel, EffectiveTheme()));

        var state = Settings.GetOrCreateFolderState(OpenedFolderPath);
        state.LastSelectedRelativePath = rel;
        await Settings.SaveAsync();
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

        var state = Settings.GetOrCreateFolderState(OpenedFolderPath);
        state.ExpandedFolders = CollectExpandedFolders();
        await Settings.SaveAsync();
    }

    public async Task PersistExpansionAsync()
    {
        if (string.IsNullOrEmpty(OpenedFolderPath)) return;
        var state = Settings.GetOrCreateFolderState(OpenedFolderPath);
        state.ExpandedFolders = CollectExpandedFolders();
        await Settings.SaveAsync();
    }

    private void RebuildRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var p in Settings.Current.RecentFolders)
        {
            RecentFolders.Add(new RecentFolderEntry(p, Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))));
        }
    }

    [RelayCommand]
    private void RevealInFileExplorer()
    {
        var path = SelectedItem?.FullPath ?? OpenedFolderPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private void CopyFilePath()
    {
        var path = SelectedItem?.FullPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch { /* best-effort */ }
    }
}

public sealed record LoadRequest(string Markdown, string RelativePath, string Theme);

public sealed record RecentFolderEntry(string FullPath, string DisplayName);
