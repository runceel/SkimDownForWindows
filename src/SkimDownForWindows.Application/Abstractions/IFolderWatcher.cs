using System;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// 開いているフォルダーを再帰的に監視する抽象。
/// 実装は debounce / UI スレッドへのマーシャリングを担当する。
/// </summary>
public interface IFolderWatcher : IDisposable
{
    /// <summary>
    /// debounce 後に呼ばれるツリー再構築イベント。引数は無し。
    /// </summary>
    event Action? TreeMayHaveChanged;

    /// <summary>
    /// debounce 後に呼ばれる Markdown ファイルの内容変更イベント。引数は絶対パス。
    /// </summary>
    event Action<string>? FileContentChanged;

    /// <summary>
    /// 指定フォルダーの監視を開始する。既に監視中の場合は停止して再開する。
    /// </summary>
    void Watch(string folderPath);

    /// <summary>
    /// 監視を停止する。<see cref="IDisposable.Dispose"/> でも自動的に呼ばれる。
    /// </summary>
    void Stop();
}
