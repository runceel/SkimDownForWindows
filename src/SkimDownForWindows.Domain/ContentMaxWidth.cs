namespace SkimDownForWindows.Domain;

/// <summary>
/// Markdown プレビュー本文の最大幅 (横幅の上限) 段階。
///
/// CSS の <c>max-width</c> として効くため、ウィンドウがこの値より狭ければ
/// 本文はウィンドウ幅にフィットし、広ければ指定段階で頭打ちになる。
/// <list type="bullet">
///   <item><see cref="Standard"/>: 760px。SkimDown 既定 (従来挙動)。</item>
///   <item><see cref="Wide"/>: 960px。</item>
///   <item><see cref="ExtraWide"/>: 1200px。</item>
///   <item><see cref="Full"/>: 上限なし (ウィンドウ幅まで広がる)。</item>
/// </list>
/// </summary>
public enum ContentMaxWidth
{
    Standard,
    Wide,
    ExtraWide,
    Full,
}
