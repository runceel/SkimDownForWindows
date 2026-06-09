namespace SkimDownForWindows.Domain;

/// <summary>
/// サイドバー (ファイル一覧) の表示モード。
///
/// <list type="bullet">
///   <item><see cref="Tree"/>: VS Code Explorer 風のフォルダー階層ツリー (フォルダー優先・名前順)。</item>
///   <item><see cref="RecentlyModified"/>: フォルダー階層を無視し、配下の全 Markdown を
///     更新日時の新しい順に並べたフラット一覧。</item>
/// </list>
/// </summary>
public enum SidebarViewMode
{
    Tree,
    RecentlyModified,
}
