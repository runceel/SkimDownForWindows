namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// アプリ自身のメタ情報 (表示名・バージョン・コピーライト・短い説明) を取得する抽象。
/// "About" ダイアログ等の UI 表示に使う。値はプロセスライフタイムで不変。
/// </summary>
/// <remarks>
/// 既定実装はパッケージ実行時に <c>Package.Current</c> から、非パッケージ実行時には
/// アセンブリ属性 (<c>AssemblyInformationalVersionAttribute</c> 等) からそれぞれ値を取得する。
/// 取得失敗時もスローせず、安全な既定値 (空文字や <c>"unknown"</c>) を返す best-effort 契約。
/// </remarks>
public interface IAppInfoService
{
    /// <summary>
    /// 表示用のアプリ名 (例: <c>"SkimDown"</c>)。
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// 表示用バージョン文字列 (例: <c>"1.0.1"</c>)。
    /// </summary>
    string Version { get; }

    /// <summary>
    /// 1〜2 行程度の短い説明 (例: <c>"A quiet, read-only Markdown folder reader."</c>)。
    /// </summary>
    string Description { get; }

    /// <summary>
    /// コピーライト表記 (例: <c>"© 2025 okazuki"</c>)。取得不能時は空文字。
    /// </summary>
    string Copyright { get; }
}
