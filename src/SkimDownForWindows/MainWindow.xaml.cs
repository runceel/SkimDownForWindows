using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Domain;
using Windows.UI;

namespace SkimDownForWindows;

/// <summary>
/// アプリケーションのメインウィンドウ。<see cref="MainPage"/> を表示するフレームをホストする。
///
/// 各ウィンドウは自前の <see cref="IServiceScope"/> を所有し、閉じられた時に dispose する。
/// スコープ内では <see cref="ViewModels.MainPageViewModel"/> や <see cref="IFolderWatcher"/>
/// などのウィンドウ寿命に紐づく Scoped サービスがライフサイクル管理される。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IServiceScope _scope;

    public MainWindow() : this(null, restoreLastFolder: true) { }

    public MainWindow(string? initialFolderPath, bool restoreLastFolder)
    {
        InitializeComponent();

        // App.Services はこのコンストラクタが呼ばれる時点で確実に初期化済み (App.OnLaunched 内で
        // ServiceProviderFactory.Build → WindowService.CreateWindow → このコンストラクタ の順)。
        _scope = App.Services.CreateScope();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        Closed += OnClosed;

        // ウィンドウ固有のランタイム引数とスコープを Page に渡す
        var startArgs = new MainPageStartArgs(this, _scope.ServiceProvider, initialFolderPath, restoreLastFolder);
        RootFrame.Navigate(typeof(MainPage), startArgs);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try { _scope.Dispose(); }
        catch { /* best-effort: scope dispose may fail if VM dispose throws */ }
    }

    /// <summary>
    /// ウィンドウタイトルを更新する。ページがフォルダーを変えた時に呼ばれる
    /// (SPEC: <c>"FolderName — SkimDown"</c>)。
    /// </summary>
    public void SetTitle(string title)
    {
        Title = title;
        AppTitleBar.Title = title;
        try { App.Services.GetRequiredService<IWindowService>().NotifyTitleChanged(); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// ユーザー選択テーマをウィンドウ全体 (TitleBar + 系統 caption ボタン) に反映する。
    ///
    /// <see cref="AppTheme.Custom"/> のときは <paramref name="customIsDark"/> で
    /// 「カスタムテーマが暗色か」を渡して caption ボタンの前景色を決定する。
    /// </summary>
    public void ApplyTheme(AppTheme theme, bool? customIsDark = null)
    {
        ElementTheme requested = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Custom => (customIsDark ?? false) ? ElementTheme.Dark : ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        RootGrid.RequestedTheme = requested;

        // OS が描画する Caption (min/max/close) ボタンは XAML テーマを継承しないため、
        // 実効テーマを ISystemThemeProvider で解決して明示色を設定する。
        var themeProvider = App.Services.GetRequiredService<ISystemThemeProvider>();
        AppTheme effective;
        if (theme == AppTheme.Custom)
        {
            effective = (customIsDark ?? false) ? AppTheme.Dark : AppTheme.Light;
        }
        else if (theme == AppTheme.System)
        {
            effective = themeProvider.ResolveSystem();
        }
        else
        {
            effective = theme;
        }

        var captionBar = AppWindow?.TitleBar;
        if (captionBar is null) return;

        captionBar.BackgroundColor = Colors.Transparent;
        captionBar.ButtonBackgroundColor = Colors.Transparent;
        captionBar.InactiveBackgroundColor = Colors.Transparent;
        captionBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (effective == AppTheme.Dark)
        {
            captionBar.ForegroundColor = Colors.White;
            captionBar.ButtonForegroundColor = Colors.White;
            captionBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A);
            captionBar.ButtonHoverBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            captionBar.ButtonHoverForegroundColor = Colors.White;
            captionBar.ButtonPressedBackgroundColor = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            captionBar.ButtonPressedForegroundColor = Colors.White;
        }
        else
        {
            captionBar.ForegroundColor = Colors.Black;
            captionBar.ButtonForegroundColor = Colors.Black;
            captionBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x76, 0x76, 0x76);
            captionBar.ButtonHoverBackgroundColor = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            captionBar.ButtonHoverForegroundColor = Colors.Black;
            captionBar.ButtonPressedBackgroundColor = Color.FromArgb(0x44, 0x00, 0x00, 0x00);
            captionBar.ButtonPressedForegroundColor = Colors.Black;
        }
    }
}

/// <summary>
/// <see cref="Microsoft.UI.Xaml.Controls.Frame.Navigate(System.Type, object)"/> で
/// <see cref="MainPage"/> に渡される起動引数。
/// </summary>
/// <param name="Window">ホストウィンドウ。</param>
/// <param name="ScopeProvider">このウィンドウ専用の DI スコープ。Page は ここから VM を解決する。</param>
/// <param name="InitialFolderPath">起動時に開くフォルダー (CLI / drop 等で指定された場合)。</param>
/// <param name="RestoreLastFolder"><see cref="InitialFolderPath"/> が <c>null</c> の時に persisted LastFolderPath を復元するか。</param>
public sealed record MainPageStartArgs(
    MainWindow Window,
    IServiceProvider ScopeProvider,
    string? InitialFolderPath,
    bool RestoreLastFolder);
