using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SkimDownForWindows.Application.CommandLine;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace SkimDownForWindows;

/// <summary>
/// 自前のプロセスエントリーポイント。
///
/// WinUI 3 はデフォルトで <c>Application.Start</c> を呼ぶ Main を自動生成するが、
/// このアプリでは <c>DISABLE_XAML_GENERATED_MAIN</c> でそれを抑止し、ここで
/// <see cref="AppInstance.FindOrRegisterForKey"/> による single-instance redirect を
/// <see cref="Application.Start"/> 前に実行する。
///
/// これにより、Explorer から複数の <c>.md</c> ファイルをダブルクリックされても
/// 既存プロセスに activation を redirect し、settings.json への書き込み競合を回避する。
/// </summary>
public static class Program
{
    /// <summary>このアプリの single-instance キー。プロセス間で 1 つの "main" を共有する。</summary>
    private const string MainInstanceKey = "SkimDownForWindowsMain";

    /// <summary>
    /// 子プロセスへの再帰起動を 1 回に抑制するためのマーカー環境変数。
    /// 親プロセスがセットし、子プロセスは値を見て即座に消す。
    /// </summary>
    private const string DetachedRelaunchMarker = "SKIMDOWNFORWINDOWS_DETACHED_RELAUNCH";

    [STAThread]
    private static int Main(string[] args)
    {
        // packaged WinUI app では console host (pwsh / cmd / WindowsTerminal 等) から alias
        // (`skimdown <path>` / `skim <path>`) で起動された場合、起動元のターミナルは子プロセスの終了まで
        // プロンプトを解放しない。本アプリは GUI subsystem (PE subsystem = 2) だが、それでも
        // 解放されないのは launcher (alias reparse point) 経由の CreateProcess が同期 spawn
        // として扱われ、shell が `WaitForSingleObject` で完了を待つため。
        //
        // 親ターミナルを即座に解放するため、ここで自分自身を再起動して即終了する。
        // - 親 (us): console host 起動を検出したら、同じ引数で子プロセスを spawn して return 0
        // - 子: マーカー env var を消した上で通常フロー (WinRT 初期化 + single-instance) へ
        //
        // 子プロセスの std handles は anonymous pipe (RedirectStandard* = true) として
        // 作り直されるため、親 (us) 終了後に PowerShell は子の I/O を待たない。
        //
        // 検出は親プロセス名 (pwsh / cmd / WindowsTerminal / conhost / OpenConsole / powershell / wt)
        // で行う。GetConsoleWindow() は packaged app の broker spawn では常に 0 を返すため使えない。
        //
        // あわせて、引数中の相対パスをここで絶対パスへ正規化する。single-instance redirect
        // (AppInstance.RedirectActivationToAsync) はコマンドライン文字列は運ぶが **カレントディレクトリは
        // 運ばない** ため、主プロセス側で相対パスを解決すると主プロセスの cwd が使われてしまう。
        // 正しい cwd を持つこのプロセスで先に絶対パス化してから子プロセス / redirect に渡す。
        var isDetachedRelaunch = string.Equals(
            Environment.GetEnvironmentVariable(DetachedRelaunchMarker), "1", StringComparison.Ordinal);
        if (isDetachedRelaunch)
        {
            // 子プロセスはマーカーを消して、孫プロセス起動や自身の再起動機能を将来追加した場合に
            // 意図せず detach 判定がスキップされないようにする。
            Environment.SetEnvironmentVariable(DetachedRelaunchMarker, null);
        }
        else
        {
            var normalizedArgs = NormalizeArgumentPaths(args);
            var pathsWereRelative = CommandLineArgumentNormalizer.DiffersFrom(args, normalizedArgs);

            // console host 起動 (親ターミナルの解放が必要) か、相対パスが含まれていて
            // 絶対パス化した引数で起動し直す必要がある場合に再起動する。
            if (IsLaunchedFromConsoleHost() || pathsWereRelative)
            {
                if (TryRelaunchDetached(normalizedArgs))
                {
                    return 0;
                }
                // 再起動に失敗した場合は通常フローへフォールバック (機能は維持される)
            }
        }

        // WinUI / WinRT の COM ラッパーをグローバルに初期化する。これは generated Main
        // でも最初に呼ばれる初期化で、欠落すると WinRT 型の marshal に失敗する。
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // この呼び出しによってカレントプロセスが "main" として登録されるか、
        // 既存の "main" が見つかるかが決まる。
        var thisInstance = AppInstance.GetCurrent();
        var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);

        if (!mainInstance.IsCurrent)
        {
            // 二次インスタンス。受け取った activation 引数を main プロセスに転送して終了する。
            // Application.Start を呼ばず、UI も作らない。
            var activatedArgs = thisInstance.GetActivatedEventArgs();
            // RedirectActivationToAsync は WinRT IAsyncAction。SynchronizationContext が
            // まだ設定されていないこの段階で GetAwaiter().GetResult() を使うのが安全
            // (deadlock しない)。.AsTask().Wait() より例外情報が読みやすい。
            mainInstance.RedirectActivationToAsync(activatedArgs).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        // 主インスタンス。後続の二次インスタンスからの redirect を受け取れるよう、
        // Activated イベントを Application 起動前に subscribe しておく
        // (Application.Start 内で App ctor → OnLaunched が走るが、その前後でも
        // 二次インスタンスからの redirect は届きうるため、ハンドラは App 側で
        // pending queue → drain する設計)。
        thisInstance.Activated += App.OnRedirectedActivation;

        WinUIApplication.Start(p =>
        {
            // WinUI が UI スレッドとして使う DispatcherQueue を取って sync context を設定する
            // (generated Main と同じ初期化)。
            var dq = DispatcherQueue.GetForCurrentThread();
            var ctx = new DispatcherQueueSynchronizationContext(dq);
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// 引数中の「実在するパスを指す位置引数」を、このプロセスのカレントディレクトリ基準で絶対パス化する。
    /// スイッチ (<c>-</c> 始まり) や存在しないパスは変更しない。
    /// </summary>
    private static string[] NormalizeArgumentPaths(string[] args)
    {
        try
        {
            return CommandLineArgumentNormalizer.ToAbsolutePaths(
                args,
                Environment.CurrentDirectory,
                static path =>
                {
                    try { return File.Exists(path) || Directory.Exists(path); }
                    catch { return false; }
                });
        }
        catch
        {
            // 正規化に失敗しても機能は維持する (相対パスのまま従来動作)
            return args;
        }
    }

    /// <summary>
    /// 親プロセスがコンソールホスト (pwsh / cmd / WindowsTerminal / conhost 等) かを判定する。
    /// packaged WinUI app では <c>GetConsoleWindow()</c> が常に 0 を返すため、親プロセス名で判定する。
    /// </summary>
    private static bool IsLaunchedFromConsoleHost()
    {
        try
        {
            var parentPid = GetParentProcessId();
            if (parentPid <= 0) return false;
            using var parent = Process.GetProcessById(parentPid);
            var name = parent.ProcessName; // 拡張子なし、大文字小文字保持
            // 主要な console host / shell プロセス
            return name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                || name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                || name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                || name.Equals("conhost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                || name.Equals("wt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 現在のプロセスの親プロセス ID を取得する。
    /// <c>NtQueryInformationProcess</c> で <c>PROCESS_BASIC_INFORMATION</c> を読む。
    /// </summary>
    private static int GetParentProcessId()
    {
        var pbi = default(NativeMethods.PROCESS_BASIC_INFORMATION);
        var status = NativeMethods.NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0, // ProcessBasicInformation
            ref pbi,
            Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(),
            out _);
        return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : -1;
    }

    /// <summary>
    /// 同じ引数で自分自身を子プロセスとして spawn する。
    /// 子の std handles は anonymous pipe に差し替えるため、PowerShell の標準入出力を継承しない。
    /// 子に <see cref="DetachedRelaunchMarker"/> env var を渡し、無限再帰を防ぐ。
    /// </summary>
    /// <returns>spawn に成功して親が即時終了すべき場合 true、フォールバックすべき場合 false。</returns>
    private static bool TryRelaunchDetached(string[] args)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            // EnvironmentVariables は親 env のコピーで初期化されるので、ここでは marker の追加だけ行う。
            psi.EnvironmentVariables[DetachedRelaunchMarker] = "1";

            // Process オブジェクトの Dispose は managed handle の解放のみで、子プロセスは kill しない。
            using var child = Process.Start(psi);
            return child is not null;
        }
        catch
        {
            // 起動失敗 (exe path 解決失敗 / 権限不足等)。通常フローへフォールバックする。
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }
    }
}
