using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>
/// フラットな Markdown ファイルパスから階層 <see cref="MarkdownTreeItem"/> ツリーを構築する。
/// VS Code Explorer と同じ並び順: フォルダー優先 → ファイル、その中で大文字小文字を区別しないアルファベット順。
/// 中身が Markdown ゼロのフォルダーは省く。
/// </summary>
public sealed class MarkdownTreeBuilder
{
    /// <summary>
    /// <paramref name="rootFolderPath"/> 配下の <paramref name="markdownFilePaths"/> からツリーを構築する。
    /// </summary>
    public MarkdownTreeItem Build(string rootFolderPath, IEnumerable<string> markdownFilePaths)
    {
        var rootName = Path.GetFileName(rootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
        {
            rootName = rootFolderPath;
        }

        var root = new MarkdownTreeItem(rootName, PathHelpers.Canonicalize(rootFolderPath), string.Empty, true);

        var rootEntry = new BuildEntry(rootName, root.FullPath, string.Empty);

        foreach (var filePath in markdownFilePaths)
        {
            if (!PathHelpers.IsInsideFolder(rootFolderPath, filePath))
            {
                continue;
            }

            var rel = PathHelpers.RelativeFromRoot(rootFolderPath, filePath);
            if (string.IsNullOrEmpty(rel))
            {
                continue;
            }

            var segments = rel.Split('/');
            var cursor = rootEntry;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (!cursor.Children.TryGetValue(segment, out var child))
                {
                    var childRel = string.IsNullOrEmpty(cursor.RelativePath) ? segment : $"{cursor.RelativePath}/{segment}";
                    var childFull = Path.Combine(cursor.FullPath, segment);
                    child = new BuildEntry(segment, childFull, childRel) { IsFolder = true };
                    cursor.Children[segment] = child;
                }
                cursor = child;
            }

            var fileName = segments[^1];
            var fileRel = rel;
            var fileFull = Path.Combine(root.FullPath, rel.Replace('/', Path.DirectorySeparatorChar));
            cursor.Children[fileName] = new BuildEntry(fileName, fileFull, fileRel) { IsFolder = false };
        }

        var totalMd = Materialize(rootEntry, root);
        root.MarkdownCount = totalMd;
        return root;
    }

    private static int Materialize(BuildEntry entry, MarkdownTreeItem target)
    {
        var folders = entry.Children.Values.Where(c => c.IsFolder)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = entry.Children.Values.Where(c => !c.IsFolder)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalMd = 0;

        foreach (var folder in folders)
        {
            var folderItem = new MarkdownTreeItem(folder.Name, folder.FullPath, folder.RelativePath, true);
            var nested = Materialize(folder, folderItem);
            if (nested == 0)
            {
                continue;
            }
            folderItem.MarkdownCount = nested;
            target.Children.Add(folderItem);
            totalMd += nested;
        }

        foreach (var file in files)
        {
            target.Children.Add(new MarkdownTreeItem(file.Name, file.FullPath, file.RelativePath, false));
            totalMd++;
        }

        return totalMd;
    }

    private sealed class BuildEntry
    {
        public string Name { get; }
        public string FullPath { get; }
        public string RelativePath { get; }
        public bool IsFolder { get; set; }
        public Dictionary<string, BuildEntry> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public BuildEntry(string name, string fullPath, string relativePath)
        {
            Name = name;
            FullPath = fullPath;
            RelativePath = relativePath;
        }
    }
}
