using Microsoft.Extensions.DependencyInjection;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Infrastructure.IO;
using SkimDownForWindows.Infrastructure.Windows;

namespace SkimDownForWindows.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure 層の既定実装を DI コンテナに登録する拡張メソッド。
/// <c>IUiDispatcher</c> と <c>IWindowService</c> はプレゼンテーション層 (WindowsAppSDK 依存) で登録する。
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Infrastructure 層の実装を Singleton で登録する。
    /// </summary>
    public static IServiceCollection AddSkimDownInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystem, LocalFileSystem>();
        services.AddSingleton<IMarkdownFileReader, LocalMarkdownFileReader>();
        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<IClipboardService, WindowsClipboardService>();
        services.AddSingleton<IShellService, ExplorerShellService>();
        services.AddSingleton<ISystemThemeProvider, UiSettingsThemeProvider>();
        services.AddSingleton<IExternalUriLauncher, LauncherExternalUriService>();
        services.AddSingleton<IAppLogger, FileAppLogger>();

        // FolderWatcher は IUiDispatcher に依存し、ウィンドウ単位でライフサイクルを管理するため Scoped。
        services.AddScoped<IFolderWatcher, FileSystemFolderWatcher>();

        return services;
    }
}
