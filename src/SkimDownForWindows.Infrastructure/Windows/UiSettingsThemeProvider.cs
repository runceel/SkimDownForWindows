using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Domain;
using Windows.UI.ViewManagement;

namespace SkimDownForWindows.Infrastructure.Windows;

/// <summary>
/// <see cref="UISettings"/> を使う <see cref="ISystemThemeProvider"/> 既定実装。
/// OS 設定の背景色から Light / Dark を判定する。
/// </summary>
public sealed class UiSettingsThemeProvider : ISystemThemeProvider
{
    public string Resolve(AppTheme userChoice)
    {
        return userChoice switch
        {
            AppTheme.Light => "light",
            AppTheme.Dark => "dark",
            _ => ResolveSystem() == AppTheme.Dark ? "dark" : "light",
        };
    }

    public AppTheme ResolveSystem()
    {
        try
        {
            var ui = new UISettings();
            var bg = ui.GetColorValue(UIColorType.Background);
            return (bg.R + bg.G + bg.B) < 384 ? AppTheme.Dark : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }
}
