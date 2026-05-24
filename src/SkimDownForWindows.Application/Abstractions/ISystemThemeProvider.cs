using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// OS のシステムテーマ (Light / Dark) を取得する抽象。
/// </summary>
public interface ISystemThemeProvider
{
    /// <summary>
    /// ユーザー選択の <paramref name="userChoice"/> を実効テーマ (<c>"light"</c> / <c>"dark"</c>) に解決する。
    /// <see cref="AppTheme.System"/> の場合のみ OS 設定を参照する。
    /// </summary>
    string Resolve(AppTheme userChoice);

    /// <summary>
    /// OS のシステムテーマだけを直接返す (<see cref="AppTheme.Light"/> または <see cref="AppTheme.Dark"/>)。
    /// </summary>
    AppTheme ResolveSystem();
}
