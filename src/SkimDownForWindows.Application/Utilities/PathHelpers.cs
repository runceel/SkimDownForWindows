using System;
using System.IO;

namespace SkimDownForWindows.Application.Utilities;

/// <summary>
/// パス正規化とフォルダー境界チェックを行う純粋ヘルパー。
/// SPEC の「開いているフォルダーの外を読まない」不変条件を担保する。
///
/// 副作用ゼロ (IO を呼ばない)。 <see cref="Path"/> ベースの計算のみで完結する。
/// </summary>
public static class PathHelpers
{
    /// <summary>
    /// 末尾の区切り文字を取り除いた絶対パスを返す。
    /// Windows 上では大文字小文字を区別しない比較がしやすい形に揃える。
    /// </summary>
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// <paramref name="candidate"/> が <paramref name="root"/> と同じか、その子孫であれば <c>true</c>。
    /// 大文字小文字を区別しない (Windows)。空文字列は拒否。
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
    /// <paramref name="root"/> からの forward-slash 相対パスを返す。
    /// <paramref name="path"/> == root の時は空文字を返す。
    /// </summary>
    /// <exception cref="InvalidOperationException">root の外なら投げる。</exception>
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
    /// ファイル名 (full path ではなく) が <c>.md</c> / <c>.markdown</c> かどうか。大文字小文字を区別しない。
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
