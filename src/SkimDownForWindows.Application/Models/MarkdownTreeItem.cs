using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SkimDownForWindows.Application.Models;

/// <summary>
/// サイドバーツリーの 1 ノード。フォルダーは <see cref="Children"/> を持ち、
/// ファイルは <see cref="IsFolder"/> が <c>false</c> の leaf。
/// </summary>
public partial class MarkdownTreeItem : ObservableObject
{
    public string Name { get; }

    public string FullPath { get; }

    /// <summary>フォルダールートからの forward-slash 相対パス。ルートは空文字。</summary>
    public string RelativePath { get; }

    public bool IsFolder { get; }

    public ObservableCollection<MarkdownTreeItem> Children { get; } = new();

    /// <summary>このノード以下にある Markdown ファイル数 (フォルダー見出し表示用)。</summary>
    public int MarkdownCount { get; set; }

    /// <summary>行アイコン用 Segoe Fluent Icons グリフ。</summary>
    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public MarkdownTreeItem(string name, string fullPath, string relativePath, bool isFolder)
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        IsFolder = isFolder;
    }
}
