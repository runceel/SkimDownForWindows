using System;
using Microsoft.UI.Xaml;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Composition;

/// <summary>
/// <see cref="MainWindow"/> を Application 層から見える <see cref="IWindowHandle"/> としてラップする。
/// </summary>
public sealed class MainWindowHandle : IWindowHandle
{
    public MainWindow Window { get; }

    public MainWindowHandle(MainWindow window)
    {
        Window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public string Title => Window.Title;

    public void Activate() => Window.Activate();

    public void Close() => Window.Close();
}
