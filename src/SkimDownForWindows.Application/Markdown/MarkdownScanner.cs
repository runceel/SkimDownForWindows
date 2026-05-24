using System.Collections.Generic;
using System.IO;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>
/// SkimDown SPEC の除外ルールに沿った再帰的な Markdown ファイル探索。
/// - 拡張子: <c>.md</c>, <c>.markdown</c> (大文字小文字を区別しない)
/// - 除外ディレクトリ (どの深さでも): <c>.git</c>, <c>node_modules</c>, <c>.build</c>, <c>DerivedData</c>
/// - 除外: 隠しファイル / 隠しフォルダー
///
/// ファイルシステム参照は <see cref="IFileSystem"/> 抽象経由で行うため、Application 層から
/// <c>System.IO</c> を直接呼び出すことはない。
/// </summary>
public sealed class MarkdownScanner
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        ".build",
        "DerivedData",
    };

    private readonly IFileSystem _fileSystem;

    public MarkdownScanner(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// <paramref name="rootFolderPath"/> 配下のすべての Markdown ファイル絶対パスを返す。
    /// 並び順は保証しない (ソートは <see cref="MarkdownTreeBuilder"/> の責務)。
    /// </summary>
    public IReadOnlyList<string> Scan(string rootFolderPath)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(rootFolderPath) || !_fileSystem.DirectoryExists(rootFolderPath))
        {
            return results;
        }

        ScanRecursive(rootFolderPath, results);
        return results;
    }

    private void ScanRecursive(string folder, List<string> results)
    {
        foreach (var entry in _fileSystem.EnumerateFileSystemEntries(folder))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (_fileSystem.IsHiddenOrSystem(entry) || name.StartsWith('.'))
            {
                continue;
            }

            if (_fileSystem.IsDirectory(entry))
            {
                if (ExcludedDirectoryNames.Contains(name))
                {
                    continue;
                }

                ScanRecursive(entry, results);
            }
            else
            {
                if (PathHelpers.IsMarkdownFile(name))
                {
                    results.Add(entry);
                }
            }
        }
    }
}
