using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.CommandLine;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Composition;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace SkimDownForWindows;

/// <summary>
/// アプリケーションのコンポジションルート。
///
/// 旧 <c>static App.DispatcherQueue</c> プロパティと旧 <c>static WindowManager</c> クラスを廃し、
/// <see cref="Services"/> をルート <see cref="IServiceProvider"/> として公開する。
/// 各 <see cref="MainWindow"/> はここから <see cref="IServiceScope"/> を作って自身のスコープとする。
///
/// 単一インスタンス redirect (<see cref="Program.Main"/> 経由) を介して 2 回目以降のアクティベーションを
/// <see cref="OnRedirectedActivation"/> で受け取り、UI スレッドに dispatch して既存 WindowService 経由で
/// 新規ウィンドウを開く。
/// </summary>
public partial class App : WinUIApplication
{
    /// <summary>
    /// プロセス全体のルート <see cref="IServiceProvider"/>。
    /// <see cref="OnLaunched(LaunchActivatedEventArgs)"/> で初期化される。
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>UI スレッドの DispatcherQueue (redirect ハンドラから UI に dispatch するため保持)。</summary>
    private static DispatcherQueue? s_uiDispatcher;

    /// <summary>Services / UI が ready になる前に届いた redirect activation を一時保管するキュー。</summary>
    private static readonly object s_pendingGate = new();
    private static readonly Queue<AppActivationArguments> s_pendingActivations = new();
    private static bool s_isReady;

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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 起動時の UI スレッドのディスパッチャを取得 (App.OnLaunched は UI スレッドで呼ばれる)
        var uiDispatcher = DispatcherQueue.GetForCurrentThread();
        s_uiDispatcher = uiDispatcher;

        // ルートの IServiceProvider を構築する。windowFactory / onLastWindowClosed の中で
        // App.Services を参照するが、ここでは遅延評価のクロージャなので循環は問題にならない
        Services = ServiceProviderFactory.Build(
            uiDispatcher,
            windowFactory: (initialActivation, restoreLastFolder) => new MainWindow(initialActivation, restoreLastFolder),
            onLastWindowClosed: ExitApp);

        // 設定をディスクからロード
        var settings = Services.GetRequiredService<ISettingsRepository>();
        settings.Load();

        // 起動時のアクティベーションを解決して 1 個目のウィンドウを開く。
        // - CLI / Launch: ILaunchActivatedEventArgs.Arguments (無ければ Environment.GetCommandLineArgs())
        //   をトークナイズして CommandLineLauncher で classify
        // - File activation (Explorer ダブルクリック): FileActivatedEventArgs.Files を Classify
        var startupActivation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var startupTargets = ExtractActivationTargets(startupActivation, isStartup: true);
        OpenFirstWindowFromActivation(startupTargets);

        // OnLaunched が走り終わったら ready とし、pending queue を drain する。
        // (Program.Main で thisInstance.Activated を subscribe しているので、
        //  Application.Start ～ ここまでの間に redirect 受信があれば queue に入っている)
        List<AppActivationArguments> pending;
        lock (s_pendingGate)
        {
            s_isReady = true;
            pending = new List<AppActivationArguments>(s_pendingActivations);
            s_pendingActivations.Clear();
        }
        foreach (var p in pending)
        {
            HandleRedirectedActivation(p);
        }
    }

    /// <summary>
    /// 二次インスタンスから redirect されたアクティベーションを受け取るハンドラ。
    /// <see cref="Program.Main"/> で <c>AppInstance.GetCurrent().Activated</c> に登録される。
    ///
    /// このハンドラは redirect を投げてきたプロセスの thread から呼ばれる可能性があるため、
    /// UI スレッドの DispatcherQueue に必ず dispatch して扱う。
    /// </summary>
    public static void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        if (e is null) return;

        // ready 前に届いた redirect は queue に貯めて OnLaunched 完了後に drain。
        lock (s_pendingGate)
        {
            if (!s_isReady || s_uiDispatcher is null)
            {
                s_pendingActivations.Enqueue(e);
                return;
            }
        }

        var dq = s_uiDispatcher!;
        dq.TryEnqueue(() => HandleRedirectedActivation(e));
    }

    private static void HandleRedirectedActivation(AppActivationArguments e)
    {
        try
        {
            var targets = ExtractActivationTargets(e, isStartup: false);
            DispatchActivationTargets(targets);
        }
        catch (Exception ex)
        {
            try { Services?.GetService<IAppLogger>()?.LogError("Redirected activation failed", ex); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// 起動時用: targets から最初の 1 ウィンドウを決める。何もなければ通常起動 (last folder 復元)。
    /// </summary>
    private static void OpenFirstWindowFromActivation(ActivationTargets targets)
    {
        var windowService = Services.GetRequiredService<IWindowService>();

        if (targets.Paths.Count == 0)
        {
            // 引数も File activation も無い → 通常起動 (last folder 復元)
            var win = windowService.CreateWindow(initialFolderPath: null, restoreLastFolder: true);
            win.Activate();
            return;
        }

        // CommandLineLauncher 経由で各 target を classify。
        // 最初の usable activation を初回ウィンドウに、残りを batch 処理。
        InitialActivation? first = null;
        var rest = new List<InitialActivation>();
        using (var startupScope = Services.CreateScope())
        {
            var cli = startupScope.ServiceProvider.GetRequiredService<CommandLineLauncher>();
            foreach (var target in targets.Paths)
            {
                var classified = cli.Classify(target, targets.CurrentDirectory);
                if (classified is null) continue;
                if (first is null) first = classified;
                else rest.Add(classified);
            }
        }

        if (first is null)
        {
            // すべての target が classify 失敗 → 通常起動扱い
            var win = windowService.CreateWindow(initialFolderPath: null, restoreLastFolder: true);
            win.Activate();
            return;
        }

        // 初回ウィンドウ: first を渡して new MainWindow(...) する (空ウィンドウを作って再利用はしない)。
        // CreateWindow は IWindowService の API 上は string? を受け取るので OpenSingleFile / OpenFolderInNewWindow を直接呼ぶ
        // (ただし「最初の 1 個目」は明示的に "再利用しない" 新規ウィンドウとして開く)。
        IWindowHandle initialWindow;
        if (first is OpenSingleFileActivation osfa)
        {
            initialWindow = windowService.OpenSingleFileInNewWindow(osfa.FilePath);
        }
        else if (first is OpenFolderActivation ofa)
        {
            initialWindow = windowService.CreateWindow(initialFolderPath: ofa.FolderPath, restoreLastFolder: false);
        }
        else
        {
            initialWindow = windowService.CreateWindow(initialFolderPath: null, restoreLastFolder: true);
        }
        initialWindow.Activate();

        // 残りは新規ウィンドウで開く (Explorer の複数ファイル選択を想定)。
        foreach (var r in rest)
        {
            if (r is OpenSingleFileActivation osfa2)
            {
                windowService.OpenSingleFileInNewWindow(osfa2.FilePath);
            }
            else if (r is OpenFolderActivation ofa2)
            {
                windowService.OpenFolderInNewWindow(ofa2.FolderPath);
            }
        }
    }

    /// <summary>
    /// Redirect 受信時用: targets を全部処理し、各 target を常に新規ウィンドウで開く。
    /// 開ける対象が 1 件も無い場合 (引数なしの <c>skim</c> など) は、コールドスタートと同じく
    /// last folder を復元した新規ウィンドウを 1 つ開く。
    /// </summary>
    private static void DispatchActivationTargets(ActivationTargets targets)
    {
        var windowService = Services.GetRequiredService<IWindowService>();

        var classifiedList = new List<InitialActivation>();
        if (targets.Paths.Count > 0)
        {
            using var scope = Services.CreateScope();
            var cli = scope.ServiceProvider.GetRequiredService<CommandLineLauncher>();
            foreach (var target in targets.Paths)
            {
                var c = cli.Classify(target, targets.CurrentDirectory);
                if (c is not null) classifiedList.Add(c);
            }
        }

        if (classifiedList.Count == 0)
        {
            // 対象なし (引数なし起動 / 全件 classify 失敗) → コールドスタートと同じ挙動で新規ウィンドウ
            var win = windowService.CreateWindow(initialFolderPath: null, restoreLastFolder: true);
            win.Activate();
            return;
        }

        foreach (var act in classifiedList)
        {
            if (act is OpenSingleFileActivation osfa)
            {
                windowService.OpenSingleFileInNewWindow(osfa.FilePath);
            }
            else if (act is OpenFolderActivation ofa)
            {
                windowService.OpenFolderInNewWindow(ofa.FolderPath);
            }
        }
    }

    /// <summary>
    /// アクティベーションから解決した「開く対象パス」と、相対パス解決に使うカレントディレクトリ。
    /// </summary>
    private readonly record struct ActivationTargets(IReadOnlyList<string> Paths, string CurrentDirectory)
    {
        public static ActivationTargets Empty => new(Array.Empty<string>(), Environment.CurrentDirectory);
    }

    /// <summary>
    /// <see cref="AppActivationArguments"/> から「開く対象パス」のリストを抽出する。
    ///
    /// - <c>File</c>: <see cref="FileActivatedEventArgs.Files"/> を絶対パスに変換
    /// - <c>CommandLineLaunch</c>: <see cref="CommandLineActivatedEventArgs"/> の
    ///   <c>Operation.Arguments</c> / <c>Operation.CurrentDirectoryPath</c> を使う
    /// - <c>Launch</c> (既定): <see cref="ILaunchActivatedEventArgs.Arguments"/> をトークナイズする
    /// - その他の kind: 空リスト
    ///
    /// 重要: <see cref="Environment.GetCommandLineArgs"/> は **主プロセス自身の起動時引数** なので、
    /// redirect 受信時 (<paramref name="isStartup"/> が <c>false</c>) には決して使わない。
    /// 使ってしまうと 2 回目以降の <c>skim &lt;path&gt;</c> が 1 回目と同じ対象を開いてしまう。
    /// </summary>
    /// <param name="args">解決対象のアクティベーション引数。</param>
    /// <param name="isStartup">
    /// このプロセス自身の起動アクティベーション (<see cref="OnLaunched"/> 経由) なら <c>true</c>。
    /// <c>true</c> のときに限り <see cref="Environment.GetCommandLineArgs"/> へのフォールバックを許可する。
    /// </param>
    private static ActivationTargets ExtractActivationTargets(AppActivationArguments? args, bool isStartup)
    {
        if (args is null) return ActivationTargets.Empty;

        // ExtendedKind は ExtendedActivationKind 列挙
        switch (args.Kind)
        {
            case ExtendedActivationKind.File:
                if (args.Data is FileActivatedEventArgs fileArgs && fileArgs.Files is not null)
                {
                    var paths = new List<string>();
                    foreach (var f in fileArgs.Files)
                    {
                        if (f is StorageFile sf && !string.IsNullOrEmpty(sf.Path))
                        {
                            paths.Add(sf.Path);
                        }
                        else if (f is StorageFolder sfd && !string.IsNullOrEmpty(sfd.Path))
                        {
                            paths.Add(sfd.Path);
                        }
                    }
                    LogActivation(args.Kind, string.Join(" ", paths), paths);
                    return new ActivationTargets(paths, Environment.CurrentDirectory);
                }
                return ActivationTargets.Empty;

            default:
                // CommandLineLaunch が届く環境では cwd も一緒に運ばれるので最優先で使う。
                if (args.Data is ICommandLineActivatedEventArgs commandLineArgs
                    && commandLineArgs.Operation is { } operation)
                {
                    var cliTargets = CommandLineTokenizer.ExtractPositionalTargets(operation.Arguments);
                    var cliCwd = string.IsNullOrWhiteSpace(operation.CurrentDirectoryPath)
                        ? Environment.CurrentDirectory
                        : operation.CurrentDirectoryPath;
                    LogActivation(args.Kind, operation.Arguments, cliTargets);
                    if (cliTargets.Count > 0)
                    {
                        return new ActivationTargets(cliTargets, cliCwd);
                    }
                    return new ActivationTargets(Array.Empty<string>(), cliCwd);
                }

                // Launch: redirect 元プロセスのコマンドライン文字列が Arguments に入る。
                if (args.Data is ILaunchActivatedEventArgs launchArgs)
                {
                    var launchTargets = CommandLineTokenizer.ExtractPositionalTargets(launchArgs.Arguments);
                    LogActivation(args.Kind, launchArgs.Arguments, launchTargets);
                    if (launchTargets.Count > 0)
                    {
                        return new ActivationTargets(launchTargets, Environment.CurrentDirectory);
                    }
                }

                if (!isStartup)
                {
                    // redirect 受信時は自プロセスのコマンドラインへフォールバックしない。
                    return ActivationTargets.Empty;
                }

                // 起動時のみ: Environment.GetCommandLineArgs() の args[1..] を使う。
                var cmd = Environment.GetCommandLineArgs();
                if (cmd.Length <= 1) return ActivationTargets.Empty;
                var list = new List<string>(cmd.Length - 1);
                for (int i = 1; i < cmd.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(cmd[i]) && !cmd[i].StartsWith('-'))
                    {
                        list.Add(cmd[i]);
                    }
                }
                LogActivation(args.Kind, "(Environment.GetCommandLineArgs)", list);
                return new ActivationTargets(list, Environment.CurrentDirectory);
        }
    }

    /// <summary>
    /// アクティベーション解決の結果をログに残す (redirect の現地調査用)。
    /// </summary>
    private static void LogActivation(ExtendedActivationKind kind, string? rawArguments, IReadOnlyList<string> targets)
    {
        try
        {
            Services?.GetService<IAppLogger>()?.LogInformation(
                $"Activation kind={kind} raw='{rawArguments}' targets=[{string.Join("; ", targets)}]");
        }
        catch { /* best-effort */ }
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
