using System;
using System.Collections.Generic;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Models;

/// <summary>
/// グローバルなアプリ設定。<c>LocalFolder</c> に JSON として永続化される。
/// フォルダーごとの状態は <see cref="FolderState"/> オブジェクトに格納される。
/// </summary>
public sealed class AppSettings
{
    /// <summary>RecentFolders の最大保持件数。</summary>
    public const int MaxRecentFolders = 16;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// <see cref="Theme"/> が <see cref="AppTheme.Custom"/> の時に有効な、登録済みカラースキーマの ID。
    /// 組み込みテーマ (System/Light/Dark) の時は <c>null</c>。
    ///
    /// JSON ペイロード上は省略可能 (<c>null</c> 書き出しは抑止される)。<see cref="NormalizeAfterLoad"/>
    /// が「Custom だが ID が無い」状態を <see cref="AppTheme.System"/> に戻す。
    /// </summary>
    public string? CustomThemeId { get; set; }

    /// <summary>WebView2 ズーム倍率 (1.0 = 100%)。範囲 0.5–3.0。</summary>
    public double ZoomFactor { get; set; } = 1.0;

    public bool SearchCaseSensitive { get; set; } = false;

    public double SidebarWidth { get; set; } = 280;

    public bool SidebarVisible { get; set; } = true;

    /// <summary>サイドバーがウィンドウの左右どちらに表示されるか。既定は <see cref="SidebarPosition.Left"/>。</summary>
    public SidebarPosition SidebarPosition { get; set; } = SidebarPosition.Left;

    /// <summary>
    /// Markdown プレビュー本文の最大幅段階。既定は <see cref="ContentMaxWidth.Standard"/> (760px)。
    /// CSS の <c>max-width</c> として効くため、ウィンドウがこの値より狭ければ本文はウィンドウ幅に
    /// フィットし、広ければ指定段階で頭打ちになる。
    /// </summary>
    public ContentMaxWidth ContentMaxWidth { get; set; } = ContentMaxWidth.Standard;

    /// <summary>直近に開いたフォルダーのパス、最新が先頭。最大 <see cref="MaxRecentFolders"/> 件。</summary>
    public List<string> RecentFolders { get; set; } = new();

    public string? LastFolderPath { get; set; }

    /// <summary>フォルダー固有状態の辞書。キーは正規化済みフォルダー絶対パス。</summary>
    public Dictionary<string, FolderState> FolderStates { get; set; } = new();

    /// <summary>
    /// ディスクから読み込んだ直後に呼ぶ、in-place な不整合修正。
    /// 現状の補正対象:
    /// <list type="bullet">
    ///   <item><c>Theme=Custom &amp;&amp; CustomThemeId が空</c> を <see cref="AppTheme.System"/> に戻す。</item>
    ///   <item><c>ContentMaxWidth</c> が enum 定義外の値 (将来バージョンからのダウングレード等) なら <see cref="ContentMaxWidth.Standard"/> に戻す。</item>
    /// </list>
    /// 登録テーマからの正規化 (該当 ID が見つからない時の戻し) は <c>ColorSchemeRegistry</c> 側で行う。
    /// </summary>
    public void NormalizeAfterLoad()
    {
        if (Theme == AppTheme.Custom && string.IsNullOrEmpty(CustomThemeId))
        {
            Theme = AppTheme.System;
        }
        if (Theme != AppTheme.Custom)
        {
            CustomThemeId = null;
        }
        if (!Enum.IsDefined(ContentMaxWidth))
        {
            ContentMaxWidth = ContentMaxWidth.Standard;
        }
    }

    /// <summary>
    /// 指定フォルダーの <see cref="FolderState"/> を取得する。未登録なら新規作成して返す。
    /// 純粋なメモリ操作で副作用は無い。
    /// </summary>
    public FolderState GetOrCreateFolderState(string folderPath)
    {
        if (!FolderStates.TryGetValue(folderPath, out var state))
        {
            state = new FolderState();
            FolderStates[folderPath] = state;
        }
        return state;
    }

    /// <summary>
    /// <see cref="RecentFolders"/> を更新する。既存エントリは大文字小文字を区別せず削除してから先頭挿入し、
    /// 上限を超えたら末尾を切り捨てる。<see cref="LastFolderPath"/> も同時に更新する。
    /// </summary>
    public void UpdateRecentFolders(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        RecentFolders.RemoveAll(p => string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folderPath);
        if (RecentFolders.Count > MaxRecentFolders)
        {
            RecentFolders.RemoveRange(MaxRecentFolders, RecentFolders.Count - MaxRecentFolders);
        }
        LastFolderPath = folderPath;
    }
}

/// <summary>
/// 特定フォルダーを開いた時の状態。直近選択ファイル・展開中サブフォルダー。
/// </summary>
public sealed class FolderState
{
    /// <summary>直近選択された Markdown ファイルの相対パス (forward-slash)。</summary>
    public string? LastSelectedRelativePath { get; set; }

    /// <summary>展開中フォルダーの相対パス (forward-slash) リスト。</summary>
    public List<string> ExpandedFolders { get; set; } = new();
}
