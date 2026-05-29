using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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

    [STAThread]
    private static int Main(string[] args)
    {
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
}
