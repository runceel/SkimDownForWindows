using System;
using System.Collections.Generic;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// 開いているメインウィンドウ群の app-wide レジストリ。
///
/// 旧 <c>static class WindowManager</c> を置き換える。実装は Singleton。
/// 実体の <c>Window</c> オブジェクト型は Application 層からは見えないため、
/// インターフェース上は <see cref="IWindowHandle"/> という弱抽象で扱う。
/// </summary>
public interface IWindowService
{
    /// <summary>登録中のウィンドウ件数。</summary>
    int Count { get; }

    /// <summary>登録中のウィンドウ一覧のスナップショット。</summary>
    IReadOnlyList<IWindowHandle> Windows { get; }

    /// <summary>ウィンドウの追加・削除・タイトル変更が発生した時に発火するイベント。</summary>
    event Action? WindowsChanged;

    /// <summary>新規ウィンドウを作成して登録し、必要なら指定フォルダーを初期表示する。</summary>
    /// <param name="initialFolderPath">非 null なら起動時にこのフォルダーを開く。</param>
    /// <param name="restoreLastFolder"><paramref name="initialFolderPath"/> が null の時に直前フォルダーを復元するか。</param>
    IWindowHandle CreateWindow(string? initialFolderPath = null, bool restoreLastFolder = true);

    /// <summary>新規ウィンドウで指定フォルダーを開く (フォルダー drop で既にフォルダーを開いているウィンドウに drop された時用)。</summary>
    IWindowHandle OpenFolderInNewWindow(string folderPath);

    /// <summary>
    /// 指定 Markdown ファイル群を single-file mode で開く。
    /// 1 個目は空 / 既に single-file mode のウィンドウがあれば再利用、
    /// 2 個目以降は常に新規ウィンドウ。
    /// </summary>
    void OpenSingleFilesInWindows(System.Collections.Generic.IReadOnlyList<string> filePaths);

    /// <summary>1 個の Markdown ファイルを必ず新規ウィンドウで single-file mode 表示する。</summary>
    IWindowHandle OpenSingleFileInNewWindow(string filePath);

    /// <summary>
    /// 1 個の Markdown ファイルを、既存の空 / single-file mode ウィンドウがあれば再利用、
    /// なければ新規ウィンドウで single-file mode 表示する。
    /// </summary>
    IWindowHandle OpenSingleFile(string filePath);

    /// <summary>指定ウィンドウを前面に持ってきてアクティブ化する。</summary>
    void ActivateWindow(IWindowHandle window);

    /// <summary>タイトル変更などサービス外でウィンドウ状態が変わった時に呼ぶフック。</summary>
    void NotifyTitleChanged();
}

/// <summary>
/// Application 層から具体的な WinUI 3 <c>Window</c> 型を見えないようにするための弱ハンドル。
/// 実装側で <c>MainWindow</c> をラップする。
/// </summary>
public interface IWindowHandle
{
    /// <summary>ウィンドウの表示タイトル。</summary>
    string Title { get; }

    /// <summary>ウィンドウのアクティブ化。</summary>
    void Activate();

    /// <summary>ウィンドウを閉じる。</summary>
    void Close();
}