using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// 固定値を返す <see cref="ISystemThemeProvider"/> テスト用実装。
/// </summary>
internal sealed class StubSystemThemeProvider : ISystemThemeProvider
{
    /// <summary><see cref="ResolveSystem"/> が返す値。既定 Light。</summary>
    public AppTheme System { get; set; } = AppTheme.Light;

    public AppTheme ResolveSystem() => System;

    public string Resolve(AppTheme userChoice) => userChoice switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => System == AppTheme.Dark ? "dark" : "light",
    };
}
