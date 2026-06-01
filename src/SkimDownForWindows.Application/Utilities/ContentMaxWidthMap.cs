using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Utilities;

/// <summary>
/// <see cref="ContentMaxWidth"/> を WebView2 の renderer に渡す CSS 値文字列に変換する純粋ヘルパー。
///
/// renderer 側は受け取った値を <c>document.body.style.setProperty("--skim-content-max", value)</c>
/// で適用する。<c>main.markdown-body { max-width: var(--skim-content-max); }</c> が
/// <c>skimdown.css</c> 側に定義されているため、CSS variable の上書きだけで本文最大幅が変わる。
/// </summary>
public static class ContentMaxWidthMap
{
    /// <summary>
    /// 段階を CSS の <c>max-width</c> 値に変換する。
    /// <see cref="ContentMaxWidth.Full"/> は上限なしを表す <c>"none"</c>。
    /// 未定義の値が来た場合は <see cref="ContentMaxWidth.Standard"/> 相当の <c>"760px"</c>
    /// にフォールバックする (永続化値が enum 範囲外で復元された場合の防御)。
    /// </summary>
    public static string ToCssValue(ContentMaxWidth value) => value switch
    {
        ContentMaxWidth.Standard => "760px",
        ContentMaxWidth.Wide => "960px",
        ContentMaxWidth.ExtraWide => "1200px",
        ContentMaxWidth.Full => "none",
        _ => "760px",
    };
}
