using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.ViewModels;
using SkimDownForWindows.Domain;
using Windows.ApplicationModel.Resources;
using Windows.Graphics;
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
    private static bool s_restoreAttemptedInThisProcess;

    private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse();
    private readonly IServiceScope _scope;
    private MainPageViewModel? _viewModel;
    private RectInt32? _pendingRestoreBounds;
    private bool _pendingMaximize;
    private bool _restorePending;
    private RectInt32? _lastRestoredBounds;
    private bool _lastWasMaximized;

    public MainWindow() : this(initialActivation: null, restoreLastFolder: true) { }

    public MainWindow(InitialActivation? initialActivation, bool restoreLastFolder)
    {
        InitializeComponent();

        Title = _strings.GetString("MainWindow/Title");
        AppTitleBar.Title = _strings.GetString("AppTitleBar/Title");

        // App.Services はこのコンストラクタが呼ばれる時点で確実に初期化済み (App.OnLaunched 内で
        // ServiceProviderFactory.Build → WindowService.CreateWindow → このコンストラクタ の順)。
        _scope = App.Services.CreateScope();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        PrepareWindowBoundsRestoreIfEnabled();
        Activated += OnActivated;
        AppWindow.Changed += OnAppWindowChanged;

        Closed += OnClosed;

        // ウィンドウ固有のランタイム引数とスコープを Page に渡す
        var startArgs = new MainPageStartArgs(this, _scope.ServiceProvider, initialActivation, restoreLastFolder);
        RootFrame.Navigate(typeof(MainPage), startArgs);

        // 起動直後のレイアウト初期化が標準位置を再適用するケースに備え、
        // message loop へ 1 tick 遅延で復元を再試行する。
        DispatcherQueue.TryEnqueue(TryApplyPendingRestoreBounds);
    }

    /// <summary>
    /// このウィンドウのスコープに紐付く <see cref="MainPageViewModel"/> を取得する。
    /// VM は <see cref="MainPage.OnNavigatedTo"/> で初めて resolve されるが、それ以降は
    /// このスコープから同一インスタンスを返す (Scoped 登録)。
    ///
    /// 単一インスタンス redirect で別ウィンドウから本ウィンドウの VM に
    /// <see cref="MainPageViewModel.OpenSingleFileAsync"/> を呼び込む時に使う。
    /// </summary>
    public MainPageViewModel GetViewModel()
    {
        return _viewModel ??= _scope.ServiceProvider.GetRequiredService<MainPageViewModel>();
    }

    /// <summary>
    /// 既存ウィンドウ再利用の判定: 何も開いていない (empty) か single-file mode のとき <c>true</c>。
    /// folder mode 中のウィンドウは再利用候補にしない (= 新規ウィンドウで開く)。
    /// </summary>
    public bool IsEmptyOrSingleFile
    {
        get
        {
            try
            {
                var vm = GetViewModel();
                return !vm.HasFolder || vm.IsSingleFileMode;
            }
            catch
            {
                // VM がまだ取れない (= まだロード中) → 「empty」扱いで再利用候補にする
                return true;
            }
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnActivated;
        AppWindow.Changed -= OnAppWindowChanged;
        PersistWindowBoundsIfEnabled();
        try { _scope.Dispose(); }
        catch { /* best-effort: scope dispose may fail if VM dispose throws */ }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        TryApplyPendingRestoreBounds();
    }

    /// <summary>
    /// ウィンドウが通常表示 (Restored) のときの位置・サイズと、最後の最大化状態を追跡する。
    /// 最大化中は位置・サイズが最大化矩形になるため、「最大化前の通常サイズ」を別途覚えておき
    /// 終了時の保存に使う。
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is not OverlappedPresenter op)
        {
            return;
        }

        switch (op.State)
        {
            case OverlappedPresenterState.Restored:
                _lastWasMaximized = false;
                var position = sender.Position;
                var size = sender.Size;
                if (size.Width > 0 && size.Height > 0)
                {
                    _lastRestoredBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
                }
                break;
            case OverlappedPresenterState.Maximized:
                _lastWasMaximized = true;
                break;
            // Minimized: 直前の通常サイズ・最大化状態を保持する
        }
    }

    private void TryApplyPendingRestoreBounds()
    {
        if (!_restorePending)
        {
            return;
        }

        _restorePending = false;

        if (_pendingRestoreBounds is RectInt32 bounds)
        {
            try
            {
                AppWindow.MoveAndResize(bounds);
            }
            catch
            {
                AppWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
                AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
            }
        }

        if (_pendingMaximize && AppWindow.Presenter is OverlappedPresenter op)
        {
            try { op.Maximize(); }
            catch { /* best-effort: presenter may reject maximize in rare states */ }
        }
    }

    private void PrepareWindowBoundsRestoreIfEnabled()
    {
        var settingsRepository = App.Services.GetService<ISettingsRepository>();
        if (settingsRepository is null)
        {
            return;
        }

        var settings = settingsRepository.Current;
        if (!settings.RememberWindowPosition)
        {
            return;
        }

        // プロセス内で最初に生成されるウィンドウにのみ復元を適用する。
        if (s_restoreAttemptedInThisProcess)
        {
            return;
        }
        s_restoreAttemptedInThisProcess = true;

        var hasStoredPosition = settings.LastWindowPositionX is not null && settings.LastWindowPositionY is not null;
        var hasStoredSize = settings.LastWindowWidth is > 0 && settings.LastWindowHeight is > 0;
        if (!hasStoredPosition && !hasStoredSize && !settings.LastWindowMaximized)
        {
            return;
        }

        if (hasStoredPosition || hasStoredSize)
        {
            var currentPosition = AppWindow.Position;
            var currentSize = AppWindow.Size;
            var requestedPosition = hasStoredPosition
                ? new PointInt32(settings.LastWindowPositionX!.Value, settings.LastWindowPositionY!.Value)
                : currentPosition;
            var requestedSize = hasStoredSize
                ? new SizeInt32(settings.LastWindowWidth!.Value, settings.LastWindowHeight!.Value)
                : currentSize;

            _pendingRestoreBounds = ClampBoundsToNearestDisplayAreaWorkArea(requestedPosition, requestedSize);
        }

        _pendingMaximize = settings.LastWindowMaximized;
        _restorePending = true;
    }

    private static RectInt32 ClampBoundsToNearestDisplayAreaWorkArea(
        PointInt32 requestedPosition,
        SizeInt32 requestedSize)
    {
        var area = DisplayArea.GetFromPoint(requestedPosition, DisplayAreaFallback.Nearest).WorkArea;
        if (area.Width <= 0 || area.Height <= 0)
        {
            return new RectInt32(requestedPosition.X, requestedPosition.Y, requestedSize.Width, requestedSize.Height);
        }

        var clampedWidth = Math.Clamp(requestedSize.Width, 1, area.Width);
        var clampedHeight = Math.Clamp(requestedSize.Height, 1, area.Height);
        var maxX = area.X + area.Width - clampedWidth;
        var maxY = area.Y + area.Height - clampedHeight;
        var clampedX = Math.Clamp(requestedPosition.X, area.X, maxX);
        var clampedY = Math.Clamp(requestedPosition.Y, area.Y, maxY);
        return new RectInt32(clampedX, clampedY, clampedWidth, clampedHeight);
    }

    private void PersistWindowBoundsIfEnabled()
    {
        var settingsRepository = App.Services.GetService<ISettingsRepository>();
        if (settingsRepository is null)
        {
            return;
        }

        var settings = settingsRepository.Current;
        if (!settings.RememberWindowPosition)
        {
            return;
        }

        // 「終了時」の状態として最後に閉じるウィンドウだけを採用する。
        var windowService = App.Services.GetService<IWindowService>();
        if (windowService is not null && windowService.Count > 1)
        {
            return;
        }

        // 現在 (または最後に) 通常表示だった時の位置・サイズを保存する。
        // 最大化・最小化中は AppWindow.Position/Size が通常サイズを表さないため、
        // OnAppWindowChanged が追跡した最後の通常サイズ (_lastRestoredBounds) を使う。
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        var isRestored = presenter is null || presenter.State == OverlappedPresenterState.Restored;
        RectInt32? normalBounds = isRestored
            ? new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height)
            : _lastRestoredBounds;

        if (normalBounds is RectInt32 bounds && bounds.Width > 0 && bounds.Height > 0)
        {
            settings.LastWindowPositionX = bounds.X;
            settings.LastWindowPositionY = bounds.Y;
            settings.LastWindowWidth = bounds.Width;
            settings.LastWindowHeight = bounds.Height;
        }

        settings.LastWindowMaximized = isRestored ? false : _lastWasMaximized;
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
/// <param name="InitialActivation">
/// 起動時に開く対象。<see cref="OpenFolderActivation"/> なら folder mode、
/// <see cref="OpenSingleFileActivation"/> なら single-file mode、<c>null</c> なら
/// <paramref name="RestoreLastFolder"/> に従う。
/// </param>
/// <param name="RestoreLastFolder"><see cref="InitialActivation"/> が <c>null</c> の時に persisted LastFolderPath を復元するか。</param>
public sealed record MainPageStartArgs(
    MainWindow Window,
    IServiceProvider ScopeProvider,
    InitialActivation? InitialActivation,
    bool RestoreLastFolder);
