using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Composition;

/// <summary>
/// 開いている <see cref="MainWindow"/> 群の app-wide レジストリ。
/// 旧 <c>static class WindowManager</c> を置き換える Singleton インスタンス実装。
///
/// 実装メモ: ウィンドウ作成時に DI スコープが必要なので、新規ウィンドウ生成のロジックは
/// 外側 (<see cref="App"/>) からデリゲートとして注入する。ここではウィンドウのレジストリ管理と
/// ライフサイクルイベント (Closed → 登録解除 + 最終ウィンドウ時のアプリ終了) のみに責務を絞る。
/// </summary>
public sealed class WindowService : IWindowService
{
    private readonly Dictionary<WindowId, MainWindowHandle> _windows = new();
    private readonly Func<string?, bool, MainWindow> _windowFactory;
    private readonly Action _onLastWindowClosed;

    public event Action? WindowsChanged;

    public IReadOnlyList<IWindowHandle> Windows => _windows.Values.Cast<IWindowHandle>().ToList();

    public int Count => _windows.Count;

    public WindowService(
        Func<string?, bool, MainWindow> windowFactory,
        Action onLastWindowClosed)
    {
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        _onLastWindowClosed = onLastWindowClosed ?? throw new ArgumentNullException(nameof(onLastWindowClosed));
    }

    public IWindowHandle CreateWindow(string? initialFolderPath = null, bool restoreLastFolder = true)
    {
        var win = _windowFactory(initialFolderPath, restoreLastFolder);
        var handle = new MainWindowHandle(win);
        _windows[win.AppWindow.Id] = handle;
        win.Closed += OnWindowClosed;
        WindowsChanged?.Invoke();
        return handle;
    }

    public IWindowHandle OpenFolderInNewWindow(string folderPath)
    {
        var handle = CreateWindow(folderPath, restoreLastFolder: false);
        handle.Activate();
        return handle;
    }

    public void ActivateWindow(IWindowHandle window)
    {
        if (window is MainWindowHandle h)
        {
            try { h.Window.AppWindow.MoveInZOrderAtTop(); }
            catch { /* AppWindow may be gone if window is mid-close */ }
            h.Window.Activate();
        }
    }

    public void NotifyTitleChanged() => WindowsChanged?.Invoke();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is MainWindow mw)
        {
            _windows.Remove(mw.AppWindow.Id);
            mw.Closed -= OnWindowClosed;
            WindowsChanged?.Invoke();
            if (_windows.Count == 0)
            {
                try { _onLastWindowClosed(); }
                catch { /* best-effort */ }
            }
        }
    }
}
