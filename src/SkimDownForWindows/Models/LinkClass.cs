namespace SkimDownForWindows.Models;

/// <summary>
/// Classification of a link clicked inside the rendered Markdown preview.
/// </summary>
public enum LinkKind
{
    /// <summary>An in-document anchor like <c>#section</c>. Handle inside the WebView.</summary>
    Anchor,

    /// <summary>A relative Markdown path inside the opened folder. Switch the selected file.</summary>
    RelativeMarkdown,

    /// <summary>A relative local file inside the opened folder that is not Markdown. Block.</summary>
    RelativeNonMarkdown,

    /// <summary>Any path that resolves outside the opened folder. Block.</summary>
    OutOfFolder,

    /// <summary>An external <c>http(s)</c> link. Open with the default browser.</summary>
    External,

    /// <summary>Anything else (e.g. <c>javascript:</c>, <c>mailto:</c>). Block.</summary>
    Blocked,
}

public sealed record LinkClassification(
    LinkKind Kind,
    string? ResolvedFullPath = null,
    string? Anchor = null,
    string? AbsoluteUri = null);
