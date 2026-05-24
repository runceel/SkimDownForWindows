using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Models;

/// <summary>
/// 「現在選択されているテーマ」を表す値オブジェクト。
///
/// <see cref="AppTheme.Custom"/> 時は <see cref="CustomThemeId"/> が有効 ID を保持する。
/// 組み込みテーマ (System/Light/Dark) の時は <see cref="CustomThemeId"/> は <c>null</c>。
///
/// invalid state (<c>Theme=Custom &amp;&amp; CustomThemeId is null/empty</c>) は生成元 (Registry / Settings) で
/// <see cref="System"/> に正規化されることを前提とする — このレコードは生成された後は妥当性を保証する。
/// </summary>
public sealed record ThemeSelection(AppTheme Theme, string? CustomThemeId)
{
    /// <summary>System / Light / Dark を表すヘルパー。</summary>
    public static readonly ThemeSelection System = new(AppTheme.System, null);
    public static readonly ThemeSelection Light = new(AppTheme.Light, null);
    public static readonly ThemeSelection Dark = new(AppTheme.Dark, null);

    /// <summary>Custom テーマ用ヘルパー。<paramref name="id"/> が空なら <see cref="System"/> を返す。</summary>
    public static ThemeSelection FromCustom(string? id)
    {
        return string.IsNullOrWhiteSpace(id) ? System : new ThemeSelection(AppTheme.Custom, id);
    }

    public bool IsCustom => Theme == AppTheme.Custom && !string.IsNullOrEmpty(CustomThemeId);
}
