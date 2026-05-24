using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.DependencyInjection;
using SkimDownForWindows.Infrastructure.DependencyInjection;

namespace SkimDownForWindows.Composition;

/// <summary>
/// アプリ全体のコンポジションルートを構築する。
/// <see cref="App"/> 起動時に 1 度だけ呼ばれ、ルートの <see cref="IServiceProvider"/> を返す。
///
/// 各 <see cref="MainWindow"/> はここから返された Provider に対して
/// <see cref="ServiceProviderServiceExtensions.CreateScope(IServiceProvider)"/> でウィンドウスコープを作る。
/// </summary>
internal static class ServiceProviderFactory
{
    /// <param name="uiDispatcher">UI スレッドのディスパッチャ (<see cref="DispatcherQueue.GetForCurrentThread"/>)。</param>
    /// <param name="windowFactory">ウィンドウ生成デリゲート。<see cref="WindowService"/> が利用する。</param>
    /// <param name="onLastWindowClosed">最終ウィンドウが閉じた時のコールバック。アプリ終了処理を呼び出す。</param>
    public static IServiceProvider Build(
        DispatcherQueue uiDispatcher,
        Func<string?, bool, MainWindow> windowFactory,
        Action onLastWindowClosed)
    {
        var services = new ServiceCollection();

        // Application 層 (Markdown 純粋サービス、CommandLineLauncher、ViewModel) — Scoped
        services.AddSkimDownApplication();

        // Infrastructure 層 (LocalFileSystem 等) — Singleton (FolderWatcher のみ Scoped)
        services.AddSkimDownInfrastructure();

        // Presentation 層: WindowsAppSDK 依存の実装はここで登録する
        services.AddSingleton<IUiDispatcher>(_ => new DispatcherQueueUiDispatcher(uiDispatcher));
        services.AddSingleton<IWindowService>(_ => new WindowService(windowFactory, onLastWindowClosed));

        return services.BuildServiceProvider(validateScopes: true);
    }
}
