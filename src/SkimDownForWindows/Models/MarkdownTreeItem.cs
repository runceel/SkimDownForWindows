using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SkimDownForWindows.Models;

/// <summary>
/// One node in the sidebar tree. Folders contain <see cref="Children"/>;
/// files are leaves with <see cref="IsFolder"/> = false.
/// </summary>
public partial class MarkdownTreeItem : ObservableObject
{
    public string Name { get; }

    public string FullPath { get; }

    /// <summary>Forward-slash relative path from the folder root. Empty for the root.</summary>
    public string RelativePath { get; }

    public bool IsFolder { get; }

    public ObservableCollection<MarkdownTreeItem> Children { get; } = new();

    /// <summary>Number of Markdown files at or below this node (for folder header).</summary>
    public int MarkdownCount { get; set; }

    /// <summary>Segoe Fluent Icons glyph for the row icon.</summary>
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
