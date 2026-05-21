using System;
using System.IO;
using SkimDownForWindows.Models;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.Markdown;

/// <summary>
/// Classifies a link target that originated inside a rendered Markdown document,
/// given the folder root and the file from which the link originated.
/// </summary>
public sealed class LinkResolver
{
    /// <param name="folderRoot">Absolute path of the opened folder.</param>
    /// <param name="originFilePath">Absolute path of the .md file the link came from.</param>
    /// <param name="href">Raw href as authored in the Markdown / HTML.</param>
    public LinkClassification Classify(string folderRoot, string originFilePath, string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return new LinkClassification(LinkKind.Blocked);
        }

        // Pure anchors.
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
                // file:// — only allow if it resolves inside the folder and is Markdown.
                return ClassifyLocalPath(folderRoot, abs.LocalPath, anchor: null);
            }
            // mailto:, javascript:, data:, custom schemes — block.
            return new LinkClassification(LinkKind.Blocked);
        }

        // Relative link: split off anchor.
        var anchor = (string?)null;
        var pathPart = href;
        var hashIndex = href.IndexOf('#');
        if (hashIndex >= 0)
        {
            anchor = href[(hashIndex + 1)..];
            pathPart = href[..hashIndex];
        }

        // Empty path = pure same-file anchor.
        if (string.IsNullOrEmpty(pathPart))
        {
            return new LinkClassification(LinkKind.Anchor, Anchor: anchor);
        }

        // URL-decode the path component before resolving on disk.
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
