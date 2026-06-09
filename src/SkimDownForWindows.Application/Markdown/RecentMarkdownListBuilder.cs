using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>
/// フラットな Markdown ファイルパスから「更新日順の一覧」用ツリーを構築する。
/// <see cref="MarkdownTreeBuilder"/> と異なりフォルダー階層は作らず、配下の全 Markdown を
/// 1 段の leaf として並べる。並び順は更新日時の新しい順 (降順)、同時刻は名前昇順、
/// さらに相対パス昇順で決定的に tie-break する。
///
/// 各 leaf には <see cref="MarkdownTreeItem.LastModified"/> と
/// <see cref="MarkdownTreeItem.RelativeFolder"/> を設定し、UI の詳細行 (日時 + フォルダー) に使う。
///
/// 更新日時は <see cref="IFileSystem.GetLastWriteTimeUtc"/> 経由で取得し、Application 層から
/// <c>System.IO</c> を直接呼ばない規約を維持する。返り値は <see cref="MarkdownTreeBuilder.Build"/>
/// と同じく root <see cref="MarkdownTreeItem"/> (Children に leaf 群、<see cref="MarkdownTreeItem.MarkdownCount"/> 設定済み) で、
/// ViewModel 側の流れを対称にする。
/// </summary>
public sealed class RecentMarkdownListBuilder
{
    private readonly IFileSystem _fileSystem;

    public RecentMarkdownListBuilder(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// <paramref name="rootFolderPath"/> 配下の <paramref name="markdownFilePaths"/> から更新日順のフラット root を構築する。
    /// </summary>
    public MarkdownTreeItem Build(string rootFolderPath, IEnumerable<string> markdownFilePaths)
    {
        var rootName = Path.GetFileName(rootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
        {
            rootName = rootFolderPath;
        }

        var root = new MarkdownTreeItem(rootName, PathHelpers.Canonicalize(rootFolderPath), string.Empty, true);

        var leaves = new List<MarkdownTreeItem>();
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

            var name = LastSegment(rel);
            var fileFull = Path.Combine(root.FullPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var leaf = new MarkdownTreeItem(name, fileFull, rel, false)
            {
                LastModified = _fileSystem.GetLastWriteTimeUtc(fileFull),
                RelativeFolder = FolderPortion(rel),
            };
            leaves.Add(leaf);
        }

        var ordered = leaves
            .OrderByDescending(l => l.LastModified ?? DateTimeOffset.MinValue)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var leaf in ordered)
        {
            root.Children.Add(leaf);
        }

        root.MarkdownCount = root.Children.Count;
        return root;
    }

    private static string LastSegment(string forwardSlashRelativePath)
    {
        var idx = forwardSlashRelativePath.LastIndexOf('/');
        return idx < 0 ? forwardSlashRelativePath : forwardSlashRelativePath[(idx + 1)..];
    }

    private static string FolderPortion(string forwardSlashRelativePath)
    {
        var idx = forwardSlashRelativePath.LastIndexOf('/');
        return idx < 0 ? string.Empty : forwardSlashRelativePath[..idx];
    }
}
