using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace SkimDownForWindows.Core;

/// <summary>
/// App-level registry of open <see cref="MainWindow"/> instances. Lets every
/// menu / drop handler create, list, activate, and close windows without
/// reaching for a static "current window" singleton.
///
/// All methods are safe to call from the UI thread (the only thread that
/// creates windows).
/// </summary>
public static class WindowManager
{
    private static readonly Dictionary<WindowId, MainWindow> _windows = new();

    /// <summary>Raised when a window is created, closed, or has its title changed.</summary>
    public static event Action? WindowsChanged;

    /// <summary>Snapshot of currently open windows.</summary>
    public static IReadOnlyList<MainWindow> Windows => _windows.Values.ToList();

    public static int Count => _windows.Count;

    /// <summary>
    /// Create a new <see cref="MainWindow"/> and register it. The caller is
    /// responsible for activating the returned window.
    /// </summary>
    /// <param name="initialFolderPath">If non-null, the new window opens this
    /// folder regardless of persisted <c>LastFolderPath</c>.</param>
    /// <param name="restoreLastFolder">When <paramref name="initialFolderPath"/>
    /// is null, controls whether the persisted <c>LastFolderPath</c> is
    /// restored. New empty windows pass <c>false</c>.</param>
    public static MainWindow CreateWindow(string? initialFolderPath = null, bool restoreLastFolder = true)
    {
        var win = new MainWindow(initialFolderPath, restoreLastFolder);
        _windows[win.AppWindow.Id] = win;
        win.Closed += OnWindowClosed;
        WindowsChanged?.Invoke();
        return win;
    }

    /// <summary>
    /// Open <paramref name="folderPath"/> in a freshly-created window. Used by
    /// the drop handler when the user drops a folder onto a window that
    /// already has one open.
    /// </summary>
    public static MainWindow OpenFolderInNewWindow(string folderPath)
    {
        var win = CreateWindow(folderPath, restoreLastFolder: false);
        win.Activate();
        return win;
    }

    /// <summary>Bring the window with the given <see cref="WindowId"/> to the front and activate it.</summary>
    public static void ActivateWindow(WindowId id)
    {
        if (_windows.TryGetValue(id, out var win))
        {
            try { win.AppWindow.MoveInZOrderAtTop(); }
            catch { /* AppWindow may be gone if window is mid-close */ }
            win.Activate();
        }
    }

    /// <summary>Fire <see cref="WindowsChanged"/> when a window title changes.</summary>
    public static void NotifyTitleChanged() => WindowsChanged?.Invoke();

    private static void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is MainWindow mw)
        {
            _windows.Remove(mw.AppWindow.Id);
            mw.Closed -= OnWindowClosed;
            WindowsChanged?.Invoke();
            if (_windows.Count == 0)
            {
                // Last window: flush any pending settings before the process exits.
                try { MainPage.FlushSharedSettings(); }
                catch { /* best-effort */ }
                try { Application.Current.Exit(); }
                catch { /* best-effort */ }
            }
        }
    }
}
