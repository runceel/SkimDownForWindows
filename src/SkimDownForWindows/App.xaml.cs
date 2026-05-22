using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SkimDownForWindows.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SkimDownForWindows;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>. All <see cref="MainWindow"/>
    /// instances live on the same UI thread, so a single dispatcher is shared.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Surface the real cause of XAML / startup crashes.
        try
        {
            var logDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logPath = System.IO.Path.Combine(logDir, "SkimDownForWindows-crash.log");
            var line = $"[{DateTimeOffset.Now:O}] Unhandled: {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}";
            System.IO.File.AppendAllText(logPath, line);
        }
        catch { /* best effort */ }
        e.Handled = true;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Honor a "skimdown <folder>" / "skimdown <file.md>" CLI invocation.
        // When a usable path is provided, open it instead of restoring the
        // previously-opened folder.
        var cliFolder = CommandLineLauncher.TryGetInitialFolderPath(
            Environment.GetCommandLineArgs(),
            Environment.CurrentDirectory);

        // The first window restores the persisted LastFolderPath if no CLI
        // folder was supplied; an explicit CLI path always wins.
        var first = cliFolder is null
            ? WindowManager.CreateWindow(initialFolderPath: null, restoreLastFolder: true)
            : WindowManager.CreateWindow(initialFolderPath: cliFolder, restoreLastFolder: false);
        first.Activate();
    }
}

