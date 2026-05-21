using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.Core;

/// <summary>
/// Wraps a recursive <see cref="FileSystemWatcher"/> over the opened folder.
/// Raises debounced events on the UI thread:
///   - <see cref="TreeMayHaveChanged"/>: a markdown file was added/removed/renamed,
///     or any directory event was seen.
///   - <see cref="FileContentChanged"/>: the file with the given absolute path was
///     written to (post-debounce).
/// On watcher buffer overflow, raises <see cref="TreeMayHaveChanged"/> as a fallback rescan trigger.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly DispatcherQueue _ui;
    private FileSystemWatcher? _watcher;
    private string? _root;
    private Timer? _treeDebounce;
    private readonly object _gate = new();

    /// <summary>Debounce delay before raising <see cref="TreeMayHaveChanged"/>.</summary>
    public TimeSpan TreeDebounce { get; set; } = TimeSpan.FromMilliseconds(250);

    public event Action? TreeMayHaveChanged;
    public event Action<string>? FileContentChanged;

    public FolderWatcher(DispatcherQueue ui)
    {
        _ui = ui;
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
        // Buffer overflow or other watcher failure → fall back to a full rescan.
        ScheduleTreeChanged();
    }

    private void OnCreatedDeletedOrRenamed(object sender, FileSystemEventArgs e)
    {
        // We can't trivially know whether the path is a directory after a Deleted/Renamed
        // event, so we always schedule a tree refresh. The rescan is cheap (debounced).
        ScheduleTreeChanged();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Only Markdown content updates are interesting for preview reload.
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
                // Never let file-watcher faults propagate into the UI thread.
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
