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

    /// <summary>Markdown プレビュー右側の Table of Contents pane を表示するか。既定は表示。</summary>
    public bool IsTableOfContentsVisible { get; set; } = true;

    /// <summary>サイドバーがウィンドウの左右どちらに表示されるか。既定は <see cref="SidebarPosition.Left"/>。</summary>
    public SidebarPosition SidebarPosition { get; set; } = SidebarPosition.Left;

    /// <summary>
    /// サイドバー (ファイル一覧) の表示モード。既定は <see cref="SidebarViewMode.Tree"/> (フォルダー階層ツリー)。
    /// <see cref="SidebarViewMode.RecentlyModified"/> で全 Markdown を更新日時の新しい順に並べたフラット一覧になる。
    /// アプリ全体で 1 つの設定として共有する (フォルダーごとではない)。
    /// </summary>
    public SidebarViewMode SidebarViewMode { get; set; } = SidebarViewMode.Tree;

    /// <summary>
    /// Markdown プレビュー本文の最大幅段階。既定は <see cref="ContentMaxWidth.Full"/> (上限なし)。
    /// CSS の <c>max-width</c> として効くため、ウィンドウがこの値より狭ければ本文はウィンドウ幅に
    /// フィットし、広ければ指定段階で頭打ちになる。
    /// </summary>
    public ContentMaxWidth ContentMaxWidth { get; set; } = ContentMaxWidth.Full;

    /// <summary>
    /// <c>true</c> のとき、<see cref="Models.OpenSingleFileActivation"/> を受けた起動で
    /// single-file mode の代わりに「対象 Markdown の親フォルダーを開く」挙動に切り替える。
    /// 既定値 (<c>false</c>) は従来どおり lightweight な single-file mode。
    /// </summary>
    public bool OpenContainingFolderOnSingleFileActivation { get; set; } = false;

    /// <summary>
    /// <c>true</c> のとき、終了時のメインウィンドウ位置とサイズを保存し、次回起動時に復元する。
    /// 既定値 (<c>false</c>) は保存・復元を行わない。
    /// </summary>
    public bool RememberWindowPosition { get; set; } = false;

    /// <summary>前回終了時のメインウィンドウ X 座標。未保存時は <c>null</c>。</summary>
    public int? LastWindowPositionX { get; set; }

    /// <summary>前回終了時のメインウィンドウ Y 座標。未保存時は <c>null</c>。</summary>
    public int? LastWindowPositionY { get; set; }

    /// <summary>前回終了時のメインウィンドウ幅。未保存時は <c>null</c>。</summary>
    public int? LastWindowWidth { get; set; }

    /// <summary>前回終了時のメインウィンドウ高さ。未保存時は <c>null</c>。</summary>
    public int? LastWindowHeight { get; set; }

    /// <summary>前回終了時にメインウィンドウが最大化されていたら <c>true</c>。復元時は通常サイズへ戻したうえで最大化する。</summary>
    public bool LastWindowMaximized { get; set; }

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
    ///   <item><c>SidebarViewMode</c> が enum 定義外の値なら <see cref="SidebarViewMode.Tree"/> に戻す。</item>
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
        if (!Enum.IsDefined(SidebarViewMode))
        {
            SidebarViewMode = SidebarViewMode.Tree;
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
