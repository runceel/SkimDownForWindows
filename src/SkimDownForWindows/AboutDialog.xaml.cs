using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using SkimDownForWindows.Application.Abstractions;
using Windows.ApplicationModel.Resources;

namespace SkimDownForWindows;

/// <summary>
/// 本家 macOS 版の標準 About パネル相当の情報を表示するモーダルダイアログ。
/// </summary>
/// <remarks>
/// 静的な情報を表示するだけで状態を持たないため ViewModel は持たない。
/// 表示すべきメタ情報は <see cref="IAppInfoService"/> から、外部 URL の起動は
/// <see cref="IExternalUriLauncher"/> から取得する (Windows.System.Launcher 直呼び禁止ルール準拠)。
/// </remarks>
public sealed partial class AboutDialog : ContentDialog
{
    private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse();
    private readonly IExternalUriLauncher _launcher;

    public AboutDialog(IAppInfoService appInfo, IExternalUriLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentNullException.ThrowIfNull(launcher);

        _launcher = launcher;

        InitializeComponent();

        DisplayNameText.Text = appInfo.DisplayName;
        VersionText.Text = string.Format(_strings.GetString("AboutDialog/Version"), appInfo.Version);
        CopyrightText.Text = appInfo.Copyright;
    }

    private async void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton btn && btn.Tag is string tag &&
            Uri.TryCreate(tag, UriKind.Absolute, out var uri))
        {
            await _launcher.LaunchAsync(uri);
        }
    }

    private async void OnLicenseLinkClick(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        if (Uri.TryCreate("https://github.com/07JP27/SkimDown/blob/main/LICENSE", UriKind.Absolute, out var uri))
        {
            await _launcher.LaunchAsync(uri);
        }
    }
}
