using System;
using System.Collections.Generic;
using System.Linq;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>
/// フォルダーを開いた直後に選択する Markdown ファイルを決定する。
/// SPEC 優先順位:
///   1. このフォルダーで前回選択していたファイル (まだ存在する場合)
///   2. ルート直下の <c>README.md</c> (大文字小文字を区別しない)
///   3. ツリーの folder-first 順で最初の Markdown ファイル
///   4. 該当無し
/// </summary>
public sealed class InitialSelectionPicker
{
    /// <summary>
    /// 起動時に選択させたいファイルの絶対パスを返す。empty state なら <c>null</c>。
    /// </summary>
    public string? Pick(MarkdownTreeItem rootTree, string? lastSelectedRelativePath)
    {
        if (rootTree is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(lastSelectedRelativePath))
        {
            var match = FindFileByRelativePath(rootTree, lastSelectedRelativePath);
            if (match is not null)
            {
                return match.FullPath;
            }
        }

        var readme = rootTree.Children
            .Where(c => !c.IsFolder)
            .FirstOrDefault(c =>
                string.Equals(c.Name, "README.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, "README.markdown", StringComparison.OrdinalIgnoreCase));
        if (readme is not null)
        {
            return readme.FullPath;
        }

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
