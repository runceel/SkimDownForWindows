using System.Collections.Generic;

namespace SkimDownForWindows.Application.Theme;

/// <summary>
/// VS Code カラーテーマのキー → SkimDown for Windows の CSS 変数名へのマッピング。
///
/// 各エントリの <c>VsCodeKeys</c> は優先順位順で、配列の先頭にマッチしたキーが優先される。
/// 一致するキーが無い場合は <see cref="FallbackPalette"/> から light / dark に応じた既定値を引く。
///
/// CSS 変数名は SkimDown for Windows の既存命名 (<c>--skim-*</c>) に合わせる。
/// 本家 macOS 版は <c>--skimdown-*</c> を使うが、Windows port では既存実装との整合性を優先する
/// (ADR-0004 参照)。
/// </summary>
public static class ColorMapping
{
    /// <summary>VS Code キー → SkimDown CSS 変数 (優先順マッピング)。</summary>
    public sealed record Entry(string CssVariable, IReadOnlyList<string> VsCodeKeys);

    /// <summary>マッピング全件 (生成順 = 適用順)。</summary>
    public static readonly IReadOnlyList<Entry> All = new Entry[]
    {
        new("--skim-bg", new[]
        {
            "editor.background",
        }),
        new("--skim-fg", new[]
        {
            "editor.foreground",
            "foreground",
        }),
        new("--skim-muted", new[]
        {
            "descriptionForeground",
            "disabledForeground",
        }),
        new("--skim-border", new[]
        {
            "panel.border",
            "editorGroup.border",
            "editorWidget.border",
            "contrastBorder",
        }),
        new("--skim-soft", new[]
        {
            "editorGroupHeader.tabsBackground",
            "editor.lineHighlightBackground",
            "sideBar.background",
        }),
        new("--skim-soft-strong", new[]
        {
            "editorWidget.background",
            "editor.background",
        }),
        new("--skim-code-bg", new[]
        {
            "editor.lineHighlightBackground",
            "editorGroupHeader.tabsBackground",
        }),
        new("--skim-table-stripe", new[]
        {
            "editorGroupHeader.tabsBackground",
            "editor.lineHighlightBackground",
        }),
        new("--skim-link", new[]
        {
            "textLink.foreground",
            "editorLink.activeForeground",
            "focusBorder",
        }),
        new("--skim-blockquote", new[]
        {
            "descriptionForeground",
            "editor.foreground",
        }),
        new("--skim-mark-bg", new[]
        {
            "editor.findMatchHighlightBackground",
        }),
        new("--skim-mark-current-bg", new[]
        {
            "editor.findMatchBackground",
        }),
    };

    /// <summary>CSS 変数名のセット (Renderer / Validator 用ホワイトリスト)。</summary>
    public static readonly IReadOnlyCollection<string> CssVariableNames = BuildNames();

    private static IReadOnlyCollection<string> BuildNames()
    {
        var set = new HashSet<string>();
        foreach (var entry in All)
        {
            set.Add(entry.CssVariable);
        }
        return set;
    }
}

/// <summary>
/// VS Code キーが欠落した場合に使う、light / dark の既定 <c>--skim-*</c> 値。
/// <c>skimdown.css</c> の <c>:root</c> と <c>body[data-theme="dark"]</c> のミラー。
/// </summary>
public static class FallbackPalette
{
    /// <summary>明色テーマの既定値。<c>skimdown.css :root</c> と同一。</summary>
    public static IReadOnlyDictionary<string, string> Light { get; } = new Dictionary<string, string>
    {
        ["--skim-bg"] = "#ffffff",
        ["--skim-fg"] = "#1f2328",
        ["--skim-muted"] = "#59636e",
        ["--skim-border"] = "#d0d7de",
        ["--skim-soft"] = "#f6f8fa",
        ["--skim-soft-strong"] = "#eaeef2",
        ["--skim-code-bg"] = "#f6f8fa",
        ["--skim-table-stripe"] = "#f6f8fa",
        ["--skim-link"] = "#0969da",
        ["--skim-blockquote"] = "#59636e",
        ["--skim-mark-bg"] = "#fff5b1",
        ["--skim-mark-current-bg"] = "#ffd33d",
    };

    /// <summary>暗色テーマの既定値。<c>skimdown.css body[data-theme="dark"]</c> と同一。</summary>
    public static IReadOnlyDictionary<string, string> Dark { get; } = new Dictionary<string, string>
    {
        ["--skim-bg"] = "#0d1117",
        ["--skim-fg"] = "#e6edf3",
        ["--skim-muted"] = "#9198a1",
        ["--skim-border"] = "#30363d",
        ["--skim-soft"] = "#161b22",
        ["--skim-soft-strong"] = "#21262d",
        ["--skim-code-bg"] = "#161b22",
        ["--skim-table-stripe"] = "#161b22",
        ["--skim-link"] = "#4493f8",
        ["--skim-blockquote"] = "#9198a1",
        ["--skim-mark-bg"] = "rgba(255, 212, 59, 0.4)",
        ["--skim-mark-current-bg"] = "rgba(255, 212, 59, 0.8)",
    };

    /// <summary><paramref name="isDark"/> に応じた既定値辞書を返す。</summary>
    public static IReadOnlyDictionary<string, string> For(bool isDark) => isDark ? Dark : Light;
}
