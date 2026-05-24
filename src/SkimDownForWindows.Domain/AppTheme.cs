namespace SkimDownForWindows.Domain;

/// <summary>
/// アプリのテーマ設定。<see cref="System"/> はホスト OS の設定に追従する。
/// </summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// サイドバーがウィンドウの左右どちらに配置されるか。
/// </summary>
public enum SidebarPosition
{
    Left,
    Right,
}
