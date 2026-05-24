namespace SkimDownForWindows.Application.Models;

/// <summary>
/// プレビューに Markdown を読み込ませる要求の値オブジェクト。
/// </summary>
public sealed record LoadRequest(string Markdown, string RelativePath, string Theme);

/// <summary>
/// 最近開いたフォルダーのメニュー表示用エントリ。
/// </summary>
public sealed record RecentFolderEntry(string FullPath, string DisplayName);
