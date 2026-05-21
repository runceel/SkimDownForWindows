using System.Collections.Generic;
using System.IO;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.Markdown;

/// <summary>
/// Recursive Markdown file discovery with the SkimDown SPEC exclusions:
/// - extensions: <c>.md</c>, <c>.markdown</c> (case-insensitive)
/// - excluded dirs (anywhere): <c>.git</c>, <c>node_modules</c>, <c>.build</c>, <c>DerivedData</c>
/// - excluded: hidden files / folders
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

    /// <summary>
    /// Returns absolute paths of every Markdown file at or below <paramref name="rootFolderPath"/>,
    /// honoring SPEC exclusions. Order is not guaranteed (sorting is the tree builder's job).
    /// </summary>
    public IReadOnlyList<string> Scan(string rootFolderPath)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(rootFolderPath) || !Directory.Exists(rootFolderPath))
        {
            return results;
        }

        ScanRecursive(rootFolderPath, results);
        return results;
    }

    private void ScanRecursive(string folder, List<string> results)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folder);
        }
        catch
        {
            // Unreadable folder: skip silently. SPEC favors quiet behavior.
            return;
        }

        foreach (var entry in entries)
        {
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(entry);
            }
            catch
            {
                continue;
            }

            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (IsHidden(attrs) || name.StartsWith('.'))
            {
                continue;
            }

            if (attrs.HasFlag(FileAttributes.Directory))
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

    private static bool IsHidden(FileAttributes attrs)
        => attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System);
}
