using System;
using System.Collections.Generic;
using System.Linq;
using SkimDownForWindows.Models;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.Markdown;

/// <summary>
/// Decides which Markdown file should be selected when a folder is opened.
/// SPEC order:
///   1. The file that was last selected in this folder (if still present)
///   2. <c>README.md</c> at the top level (case-insensitive)
///   3. The first Markdown file in the tree (folder-first ordered)
///   4. No selection
/// </summary>
public sealed class InitialSelectionPicker
{
    /// <summary>
    /// Returns the absolute path of the file to pre-select, or <c>null</c> for empty state.
    /// </summary>
    public string? Pick(MarkdownTreeItem rootTree, string? lastSelectedRelativePath)
    {
        if (rootTree is null)
        {
            return null;
        }

        // 1. Last selected.
        if (!string.IsNullOrEmpty(lastSelectedRelativePath))
        {
            var match = FindFileByRelativePath(rootTree, lastSelectedRelativePath);
            if (match is not null)
            {
                return match.FullPath;
            }
        }

        // 2. Top-level README.md / README.markdown.
        var readme = rootTree.Children
            .Where(c => !c.IsFolder)
            .FirstOrDefault(c =>
                string.Equals(c.Name, "README.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, "README.markdown", StringComparison.OrdinalIgnoreCase));
        if (readme is not null)
        {
            return readme.FullPath;
        }

        // 3. First Markdown file in the depth-first folder-first order.
        var first = FindFirstFile(rootTree);
        return first?.FullPath;
    }

    private static MarkdownTreeItem? FindFileByRelativePath(MarkdownTreeItem root, string relativePath)
    {
        foreach (var leaf in EnumerateFiles(root))
        {
            if (string.Equals(leaf.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return leaf;
            }
        }
        return null;
    }

    private static MarkdownTreeItem? FindFirstFile(MarkdownTreeItem root)
        => EnumerateFiles(root).FirstOrDefault();

    private static IEnumerable<MarkdownTreeItem> EnumerateFiles(MarkdownTreeItem node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsFolder)
            {
                foreach (var leaf in EnumerateFiles(child))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return child;
            }
        }
    }
}
