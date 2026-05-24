using System;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// テスト側からイベントを発火できる <see cref="IFolderWatcher"/> テスト用実装。
/// <see cref="Watch"/> / <see cref="Stop"/> / <see cref="Dispose"/> の呼び出し回数と
/// 最後に <see cref="Watch"/> に渡されたパスを記録する。
/// </summary>
internal sealed class FakeFolderWatcher : IFolderWatcher
{
    public event Action? TreeMayHaveChanged;
    public event Action<string>? FileContentChanged;

    public int WatchCalls { get; private set; }

    public int StopCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public string? LastWatchedPath { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Watch(string folderPath)
    {
        WatchCalls++;
        LastWatchedPath = folderPath;
    }

    public void Stop()
    {
        StopCalls++;
    }

    public void Dispose()
    {
        DisposeCalls++;
        IsDisposed = true;
    }

    /// <summary>本物の watcher が UI スレッドで発火するのを模す。</summary>
    public void RaiseTreeMayHaveChanged() => TreeMayHaveChanged?.Invoke();

    /// <summary>本物の watcher が UI スレッドで発火するのを模す。</summary>
    public void RaiseFileContentChanged(string absolutePath) => FileContentChanged?.Invoke(absolutePath);
}
