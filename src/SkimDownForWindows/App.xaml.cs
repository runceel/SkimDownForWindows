using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.CommandLine;
using SkimDownForWindows.Composition;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace SkimDownForWindows;

/// <summary>
/// アプリケーションのコンポジションルート。
///
/// 旧 <c>static App.DispatcherQueue</c> プロパティと旧 <c>static WindowManager</c> クラスを廃し、
/// <see cref="Services"/> をルート <see cref="IServiceProvider"/> として公開する。
/// 各 <see cref="MainWindow"/> はここから <see cref="IServiceScope"/> を作って自身のスコープとする。
/// </summary>
public partial class App : WinUIApplication
{
    /// <summary>
    /// プロセス全体のルート <see cref="IServiceProvider"/>。
    /// <see cref="OnLaunched(LaunchActivatedEventArgs)"/> で初期化される。
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 起動 / XAML 例外の真因をディスクに残す。Services 初期化前に呼ばれることもあるため
        // ロガーは DI からではなくフォールバック的に IAppLogger を直接 new せず File.AppendAllText で書く。
        try
        {
            var logger = Services?.GetService<IAppLogger>();
            if (logger is not null)
            {
                logger.LogError($"Unhandled: {e.Message}", e.Exception);
            }
            else
            {
                var logDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var logPath = System.IO.Path.Combine(logDir, "SkimDownForWindows-crash.log");
                var line = $"[{DateTimeOffset.Now:O}] Unhandled (pre-DI): {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}";
                System.IO.File.AppendAllText(logPath, line);
            }
        }
        catch { /* best effort */ }
        e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 起動時の UI スレッドのディスパッチャを取得 (App.OnLaunched は UI スレッドで呼ばれる)
        var uiDispatcher = DispatcherQueue.GetForCurrentThread();

        // ルートの IServiceProvider を構築する。windowFactory / onLastWindowClosed の中で
        // App.Services を参照するが、ここでは遅延評価のクロージャなので循環は問題にならない
        Services = ServiceProviderFactory.Build(
            uiDispatcher,
            windowFactory: (initialFolderPath, restoreLastFolder) => new MainWindow(initialFolderPath, restoreLastFolder),
            onLastWindowClosed: ExitApp);

        // 設定をディスクからロード
        var settings = Services.GetRequiredService<ISettingsRepository>();
        settings.Load();

        // "skimdown <folder>" / "skimdown <file.md>" CLI 起動を尊重する。
        // CLI 用に一時スコープを作って CommandLineLauncher を解決する。
        string? cliFolder;
        using (var startupScope = Services.CreateScope())
        {
            var cli = startupScope.ServiceProvider.GetRequiredService<CommandLineLauncher>();
            cliFolder = cli.TryGetInitialFolderPath(
                Environment.GetCommandLineArgs(),
                Environment.CurrentDirectory);
        }

        var windowService = Services.GetRequiredService<IWindowService>();
        var first = cliFolder is null
            ? windowService.CreateWindow(initialFolderPath: null, restoreLastFolder: true)
            : windowService.CreateWindow(initialFolderPath: cliFolder, restoreLastFolder: false);
        first.Activate();
    }

    /// <summary>
    /// 最終ウィンドウが閉じた時のアプリ終了処理。
    /// 設定の最終フラッシュを行ってから <see cref="Application.Exit"/> を呼ぶ。
    /// </summary>
    private static void ExitApp()
    {
        try { Services.GetRequiredService<ISettingsRepository>().FlushSync(); }
        catch { /* best-effort */ }
        try { WinUIApplication.Current.Exit(); }
        catch { /* best-effort */ }
    }
}
