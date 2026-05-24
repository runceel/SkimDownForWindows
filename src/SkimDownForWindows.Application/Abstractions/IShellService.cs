namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// OS のシェル機能 (ファイラーでの表示など) を呼び出す抽象。
/// </summary>
public interface IShellService
{
    /// <summary>
    /// ファイラーで <paramref name="path"/> を表示する。
    /// パスがディレクトリならそれを開き、ファイルなら親フォルダーを開きそのファイルを選択する。
    /// 失敗時もスローしない (best-effort)。
    /// </summary>
    void Reveal(string path);
}
