using System;
using System.IO;

namespace SkimDownForWindows.Utilities;

/// <summary>
/// Path helpers that perform canonicalization and folder-boundary checks.
/// Used to enforce the "only read inside the opened folder" invariant from SPEC.
/// </summary>
public static class PathHelpers
{
    /// <summary>
    /// Returns a canonical full path with a trailing directory separator for folders.
    /// Uses case-insensitive comparison-friendly form for Windows.
    /// </summary>
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // Resolve to absolute and normalize separators / case.
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// True if <paramref name="candidate"/> resolves to a path that is the same as,
    /// or a descendant of, <paramref name="root"/>. Case-insensitive (Windows).
    /// Rejects empty paths.
    /// </summary>
    public static bool IsInsideFolder(string root, string candidate)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var rootFull = Canonicalize(root);
        var candFull = Canonicalize(candidate);

        if (string.Equals(rootFull, candFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSep = rootFull + Path.DirectorySeparatorChar;
        return candFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a forward-slash relative path of <paramref name="path"/> against <paramref name="root"/>.
    /// Returns empty string if <paramref name="path"/> == root.
    /// Throws <see cref="InvalidOperationException"/> if path is outside root.
    /// </summary>
    public static string RelativeFromRoot(string root, string path)
    {
        if (!IsInsideFolder(root, path))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the folder root '{root}'.");
        }

        var rel = Path.GetRelativePath(Canonicalize(root), Canonicalize(path));
        return rel == "." ? string.Empty : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// True if the file name (not full path) is a Markdown file (case-insensitive).
    /// </summary>
    public static bool IsMarkdownFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }
}
