using Microsoft.Extensions.DependencyInjection;
using SkimDownForWindows.Application.CommandLine;
using SkimDownForWindows.Application.Markdown;
using SkimDownForWindows.Application.Theme;
using SkimDownForWindows.Application.ViewModels;

namespace SkimDownForWindows.Application.DependencyInjection;

/// <summary>
/// Application 層のサービス・ViewModel を DI コンテナに登録する拡張メソッド。
/// 抽象 (<c>IFileSystem</c> 等) の実装登録は Infrastructure 側で行う。
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Markdown 純粋サービス、CommandLineLauncher、MainPageViewModel をすべて Scoped で登録する。
    /// ウィンドウごとに <see cref="IServiceScope"/> を作成して使うことを前提とする。
    /// <see cref="ColorSchemeRegistry"/> はアプリ全体で 1 つの状態を共有するため Singleton。
    /// </summary>
    public static IServiceCollection AddSkimDownApplication(this IServiceCollection services)
    {
        services.AddScoped<MarkdownScanner>();
        services.AddScoped<MarkdownTreeBuilder>();
        services.AddScoped<InitialSelectionPicker>();
        services.AddScoped<LinkResolver>();
        services.AddScoped<CommandLineLauncher>();
        services.AddScoped<MainPageViewModel>();

        services.AddSingleton<ColorSchemeRegistry>();

        return services;
    }
}
