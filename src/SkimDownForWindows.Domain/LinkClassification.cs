namespace SkimDownForWindows.Domain;

/// <summary>
/// レンダーされた Markdown プレビュー上でクリックされたリンクの分類。
/// </summary>
public enum LinkKind
{
    /// <summary>同一ドキュメント内アンカー (<c>#section</c>)。WebView 内で処理。</summary>
    Anchor,

    /// <summary>開いているフォルダー内の相対 Markdown パス。選択ファイルを切り替える。</summary>
    RelativeMarkdown,

    /// <summary>開いているフォルダー内だが Markdown でない相対ファイル。ブロックする。</summary>
    RelativeNonMarkdown,

    /// <summary>開いているフォルダーの外を指すパス。ブロックする。</summary>
    OutOfFolder,

    /// <summary>外部 <c>http(s)</c> リンク。既定ブラウザで開く。</summary>
    External,

    /// <summary>その他 (<c>javascript:</c>, <c>mailto:</c> 等)。ブロックする。</summary>
    Blocked,
}

/// <summary>
/// <see cref="LinkKind"/> と付随情報を保持する分類結果。
/// </summary>
public sealed record LinkClassification(
    LinkKind Kind,
    string? ResolvedFullPath = null,
    string? Anchor = null,
    string? AbsoluteUri = null);
