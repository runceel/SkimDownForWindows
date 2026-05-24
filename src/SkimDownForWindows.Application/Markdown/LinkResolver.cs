using System;
using System.IO;
using SkimDownForWindows.Application.Utilities;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>
/// レンダー済み Markdown 内で発生したリンクの分類器。
/// 開いているフォルダーとリンク元ファイルが既知の前提で、href を <see cref="LinkClassification"/> に解決する。
/// 副作用ゼロ (IO 無し)。
/// </summary>
public sealed class LinkResolver
{
    /// <param name="folderRoot">開いているフォルダーの絶対パス。</param>
    /// <param name="originFilePath">リンク元 .md ファイルの絶対パス。</param>
    /// <param name="href">Markdown / HTML 上の生 href。</param>
    public LinkClassification Classify(string folderRoot, string originFilePath, string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return new LinkClassification(LinkKind.Blocked);
        }

        if (href.StartsWith('#'))
        {
            return new LinkClassification(LinkKind.Anchor, Anchor: href[1..]);
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
        {
            if (abs.Scheme is "http" or "https")
            {
                return new LinkClassification(LinkKind.External, AbsoluteUri: abs.ToString());
            }
            if (abs.Scheme is "file")
            {
                return ClassifyLocalPath(folderRoot, abs.LocalPath, anchor: null);
            }
            return new LinkClassification(LinkKind.Blocked);
        }

        var anchor = (string?)null;
        var pathPart = href;
        var hashIndex = href.IndexOf('#');
        if (hashIndex >= 0)
        {
            anchor = href[(hashIndex + 1)..];
            pathPart = href[..hashIndex];
        }

        if (string.IsNullOrEmpty(pathPart))
        {
            return new LinkClassification(LinkKind.Anchor, Anchor: anchor);
        }

        try
        {
            pathPart = Uri.UnescapeDataString(pathPart);
        }
        catch
        {
            return new LinkClassification(LinkKind.Blocked);
        }

        var originDir = Path.GetDirectoryName(originFilePath) ?? folderRoot;
        var combined = Path.GetFullPath(Path.Combine(originDir, pathPart));

        return ClassifyLocalPath(folderRoot, combined, anchor);
    }

    private static LinkClassification ClassifyLocalPath(string folderRoot, string resolvedFullPath, string? anchor)
    {
        if (!PathHelpers.IsInsideFolder(folderRoot, resolvedFullPath))
        {
            return new LinkClassification(LinkKind.OutOfFolder);
        }

        if (PathHelpers.IsMarkdownFile(Path.GetFileName(resolvedFullPath)))
        {
            return new LinkClassification(LinkKind.RelativeMarkdown, ResolvedFullPath: resolvedFullPath, Anchor: anchor);
        }

        return new LinkClassification(LinkKind.RelativeNonMarkdown, ResolvedFullPath: resolvedFullPath);
    }
}
