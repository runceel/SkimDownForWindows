using System;
using System.IO;
using System.Threading;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// 再帰的 <see cref="FileSystemWatcher"/> を <see cref="IFolderWatcher"/> でラップした既定実装。
/// UI スレッドへのマーシャリングは <see cref="IUiDispatcher"/> で行う。
/// </summary>
public sealed class FileSystemFolderWatcher : IFolderWatcher
{
    private readonly IUiDispatcher _ui;
    private FileSystemWatcher? _watcher;
    private string? _root;
    private Timer? _treeDebounce;
    private readonly object _gate = new();

    /// <summary>デバウンス遅延 (<see cref="TreeMayHaveChanged"/> 発火前)。</summary>
    public TimeSpan TreeDebounce { get; set; } = TimeSpan.FromMilliseconds(250);

    public event Action? TreeMayHaveChanged;
    public event Action<string>? FileContentChanged;

    public FileSystemFolderWatcher(IUiDispatcher uiDispatcher)
    {
        _ui = uiDispatcher;
    }

    public void Watch(string folderPath)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        _root = PathHelpers.Canonicalize(folderPath);
        var w = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                          | NotifyFilters.DirectoryName
                          | NotifyFilters.LastWrite
                          | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        w.Created += OnCreatedDeletedOrRenamed;
        w.Deleted += OnCreatedDeletedOrRenamed;
        w.Renamed += OnCreatedDeletedOrRenamed;
        w.Changed += OnChanged;
        w.Error += OnError;
        w.EnableRaisingEvents = true;
        _watcher = w;
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow / その他のウォッチャー障害 → 全鳴を発火させて上位に再走査させる
        ScheduleTreeChanged();
    }

    private void OnCreatedDeletedOrRenamed(object sender, FileSystemEventArgs e)
    {
        if (ShouldScheduleTreeChanged(e))
        {
            ScheduleTreeChanged();
        }
    }

    private static bool ShouldScheduleTreeChanged(FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath))
        {
            return false;
        }

        if (IsMarkdownPath(e.FullPath))
        {
            return true;
        }

        if (e is RenamedEventArgs renamed && IsMarkdownPath(renamed.OldFullPath))
        {
            return true;
        }

        if (e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed)
        {
            return Directory.Exists(e.FullPath);
        }

        return e.ChangeType == WatcherChangeTypes.Deleted;
    }

    private static bool IsMarkdownPath(string path)
        => PathHelpers.IsMarkdownFile(Path.GetFileName(path));

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath) || !PathHelpers.IsMarkdownFile(Path.GetFileName(e.FullPath)))
        {
            return;
        }

        var path = e.FullPath;
        _ui.TryEnqueue(() =>
        {
            try
            {
                FileContentChanged?.Invoke(path);
            }
            catch
            {
                // ファイル監視のエラーを UI スレッドに伝播させない
            }
        });
    }

    private void ScheduleTreeChanged()
    {
        lock (_gate)
        {
            _treeDebounce?.Dispose();
            _treeDebounce = new Timer(_ =>
            {
                _ui.TryEnqueue(() =>
                {
                    try
                    {
                        TreeMayHaveChanged?.Invoke();
                    }
                    catch
                    {
                        // Swallow.
                    }
                });
            }, null, TreeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreatedDeletedOrRenamed;
            _watcher.Deleted -= OnCreatedDeletedOrRenamed;
            _watcher.Renamed -= OnCreatedDeletedOrRenamed;
            _watcher.Changed -= OnChanged;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_gate)
        {
            _treeDebounce?.Dispose();
            _treeDebounce = null;
        }
    }

    public void Dispose() => Stop();
}
