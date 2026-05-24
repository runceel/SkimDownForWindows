namespace SkimDownForWindows.Domain;

/// <summary>
/// アプリのテーマ設定。
///
/// <list type="bullet">
///   <item><see cref="System"/>: ホスト OS の設定 (Light / Dark) に追従する。</item>
///   <item><see cref="Light"/> / <see cref="Dark"/>: 組み込みパレット。</item>
///   <item><see cref="Custom"/>: ユーザーが登録した VS Code 互換テーマ。
///     具体的な ID は <c>AppSettings.CustomThemeId</c> に保持される。</item>
/// </list>
/// </summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
    Custom,
}

/// <summary>
/// サイドバーがウィンドウの左右どちらに配置されるか。
/// </summary>
public enum SidebarPosition
{
    Left,
    Right,
}
