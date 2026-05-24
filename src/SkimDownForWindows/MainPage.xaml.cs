using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Input;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.ViewModels;
using SkimDownForWindows.Composition;
using SkimDownForWindows.Domain;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace SkimDownForWindows;

public sealed partial class MainPage : Page
{
    private MainWindow? _window;
    private IServiceProvider? _scopeProvider;
    private IAppLogger? _logger;
    private IWindowService? _windowService;
    private bool _windowsChangedSubscribed;
    private string? _initialFolderPath;
    private bool _restoreLastFolder = true;

    /// <summary>
    /// ページが束ねる ViewModel。<see cref="OnNavigatedTo(NavigationEventArgs)"/> でスコープから解決される。
    /// XAML の <c>{x:Bind ViewModel...}</c> から参照されるため非 null として公開する (実体は OnNavigatedTo 後に有効)。
    /// </summary>
    public MainPageViewModel ViewModel { get; private set; } = null!;

    public MainPage()
    {
        try
        {
            InitializeComponent();

            Preview.RelativeMarkdownLinkClicked += OnPreviewRelativeLink;
            Preview.ExternalLinkClicked += OnPreviewExternalLink;
            Preview.SearchResult += OnPreviewSearchResult;
            Preview.ShortcutInvoked += OnPreviewShortcut;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            DragOver += OnDragOver;
            Drop += OnDrop;

            // ChildHWND がフォーカスを持っている時でも KeyDown を受け取れるように handledEventsToo:true。
            // メインロウの Ctrl+= / Ctrl+- ズームを WinUI 側でフォローするのに使う。
            AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), handledEventsToo: true);
        }
        catch (Exception ex)
        {
            // App.Services が未初期化 / DI ロガーが未準備の段階で来ることもあるので、
            // ここはローカルファイルへの直接書き込みでフォールバック。
            try
            {
                var logDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var logPath = Path.Combine(logDir, "SkimDownForWindows-crash.log");
                File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] MainPage ctor: {ex}\r\n");
            }
            catch { }
            throw;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainPageStartArgs args)
        {
            _window = args.Window;
            _scopeProvider = args.ScopeProvider;
            _initialFolderPath = args.InitialFolderPath;
            _restoreLastFolder = args.RestoreLastFolder;

            // スコープから VM・横断サービスを取得 (この時点で VM は IFolderWatcher 等を保持済み)
            ViewModel = _scopeProvider.GetRequiredService<MainPageViewModel>();
            _logger = _scopeProvider.GetRequiredService<IAppLogger>();
            _windowService = _scopeProvider.GetRequiredService<IWindowService>();

            DataContext = ViewModel;

            ViewModel.PreviewLoadRequested += OnPreviewLoadRequested;
            ViewModel.PreviewClearRequested += OnPreviewClearRequested;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            UpdateContentVisibility();
            UpdateThemeMenuChecks();
            UpdateMoveSidebarLabel();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WebView2 をバンドル済み web フォルダーで初期化
        var appWeb = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        await Preview.InitializeAsync(appWeb);

        var settings = ViewModel.Settings.Current;

        // ペルシステンス済みサイドバー幅 / visibility / 位置を、開くフォルダーを決める前に適用する
        ApplySidebarPosition(settings.SidebarPosition);
        if (settings.SidebarVisible)
        {
            SetActiveSidebarWidth(new GridLength(Math.Max(180, settings.SidebarWidth)));
            Sidebar.Visibility = Visibility.Visible;
        }
        else
        {
            SetActiveSidebarWidth(new GridLength(0));
            Sidebar.Visibility = Visibility.Collapsed;
        }

        Preview.SetZoom(settings.ZoomFactor);

        // ページとウィンドウに永続テーマを適用
        RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _window?.ApplyTheme(settings.Theme);
        Preview.SetTheme(ViewModel.EffectiveTheme());

        // どのフォルダーを開くか決定する (一度だけ):
        //   1. 明示的な initialFolderPath が最優先
        //   2. _restoreLastFolder が true で LastFolderPath が有効ならそれを復元
        //   3. 上記いずれもなければ empty 状態のまま
        string? folderToOpen = null;
        if (!string.IsNullOrEmpty(_initialFolderPath) && Directory.Exists(_initialFolderPath))
        {
            folderToOpen = _initialFolderPath;
        }
        else if (_restoreLastFolder &&
                 !string.IsNullOrEmpty(settings.LastFolderPath) &&
                 Directory.Exists(settings.LastFolderPath))
        {
            folderToOpen = settings.LastFolderPath;
        }

        if (folderToOpen is not null)
        {
            await ViewModel.OpenFolderAsync(folderToOpen);
        }

        BuildRecentFoldersMenu();
        UpdateMarkdownCount();
        UpdateWindowTitle();
        UpdateContentVisibility();
        RebuildWindowMenu();

        if (!_windowsChangedSubscribed && _windowService is not null)
        {
            _windowService.WindowsChanged += OnWindowsChanged;
            _windowsChangedSubscribed = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PreviewLoadRequested -= OnPreviewLoadRequested;
            ViewModel.PreviewClearRequested -= OnPreviewClearRequested;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        Preview.RelativeMarkdownLinkClicked -= OnPreviewRelativeLink;
        Preview.ExternalLinkClicked -= OnPreviewExternalLink;
        Preview.SearchResult -= OnPreviewSearchResult;
        Preview.ShortcutInvoked -= OnPreviewShortcut;

        if (_windowsChangedSubscribed && _windowService is not null)
        {
            _windowService.WindowsChanged -= OnWindowsChanged;
            _windowsChangedSubscribed = false;
        }

        // ViewModel と IFolderWatcher のライフサイクルは MainWindow が所有する
        // IServiceScope.Dispose() で纏めて行われる (Window.Closed 時)。ここでは追加処理は不要。
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainPageViewModel.HasFolder):
            case nameof(MainPageViewModel.HasAnyMarkdown):
            case nameof(MainPageViewModel.SelectedItem):
                UpdateContentVisibility();
                break;
            case nameof(MainPageViewModel.MarkdownCount):
                UpdateMarkdownCount();
                break;
            case nameof(MainPageViewModel.WindowTitle):
                UpdateWindowTitle();
                break;
        }
    }

    private void UpdateContentVisibility()
    {
        EmptyState.Visibility = ViewModel.HasFolder ? Visibility.Collapsed : Visibility.Visible;
        var folderButHasNoMd = ViewModel.HasFolder && !ViewModel.HasAnyMarkdown;
        NoMarkdownState.Visibility = folderButHasNoMd ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = Visibility.Visible;
    }

    private void UpdateMarkdownCount()
    {
        MarkdownCountText.Text = ViewModel.MarkdownCount == 1
            ? "1 markdown file"
            : $"{ViewModel.MarkdownCount} markdown files";
    }

    private void UpdateWindowTitle()
    {
        _window?.SetTitle(ViewModel.WindowTitle);
    }

    private void BuildRecentFoldersMenu()
    {
        RecentFoldersMenu.Items.Clear();
        if (ViewModel.RecentFolders.Count == 0)
        {
            var empty = new MenuFlyoutItem { Text = "(no recent folders)", IsEnabled = false };
            RecentFoldersMenu.Items.Add(empty);
            return;
        }

        foreach (var entry in ViewModel.RecentFolders)
        {
            var mi = new MenuFlyoutItem { Text = entry.DisplayName };
            ToolTipService.SetToolTip(mi, entry.FullPath);
            var pathCopy = entry.FullPath;
            mi.Click += async (_, _) => await ViewModel.OpenFolderAsync(pathCopy);
            RecentFoldersMenu.Items.Add(mi);
        }

        RecentFoldersMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear Recent" };
        clear.Click += async (_, _) =>
        {
            ViewModel.Settings.Current.RecentFolders.Clear();
            await ViewModel.Settings.SaveAsync();
            ViewModel.RecentFolders.Clear();
            BuildRecentFoldersMenu();
        };
        RecentFoldersMenu.Items.Add(clear);
    }

    // ----- Menu / button handlers -----

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e) => await PromptForFolderAsync();

    private async Task PromptForFolderAsync()
    {
        if (_window is null) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await ViewModel.OpenFolderAsync(folder.Path);
            BuildRecentFoldersMenu();
        }
        catch (Exception ex)
        {
            _logger?.LogError("OpenFolder failed", ex);
        }
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        _window?.Close();
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is MarkdownTreeItem item && !item.IsFolder)
        {
            _ = ViewModel.SelectAndLoadAsync(item.FullPath);
        }
    }

    // ----- Drag/drop folder onto window -----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            if (ViewModel.HasFolder)
            {
                e.DragUIOverride.Caption = "Open in new SkimDown window";
            }
            else
            {
                e.DragUIOverride.Caption = "Open in SkimDown";
            }
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var folder = items.OfType<StorageFolder>().FirstOrDefault();

            string? folderPath = folder?.Path;
            if (folderPath is null)
            {
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file is not null)
                {
                    var parent = Path.GetDirectoryName(file.Path);
                    if (!string.IsNullOrEmpty(parent)) folderPath = parent;
                }
            }
            if (string.IsNullOrEmpty(folderPath)) return;

            // SPEC: フォルダーを既に開いているウィンドウに drop した場合は新ウィンドウで開く
            if (ViewModel.HasFolder)
            {
                _windowService?.OpenFolderInNewWindow(folderPath);
            }
            else
            {
                await ViewModel.OpenFolderAsync(folderPath);
                BuildRecentFoldersMenu();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError("Drop failed", ex);
        }
    }

    // ----- Preview events -----

    private void OnPreviewLoadRequested(LoadRequest req)
    {
        // Task コンティニュエーションから来る可能性があるため UI スレッドにマーシャル
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(ViewModel.OpenedFolderPath))
            {
                Preview.SetContentFolder(ViewModel.OpenedFolderPath);
            }
            _ = Preview.LoadAsync(req.Markdown, req.RelativePath, req.Theme);
        });
    }

    private void OnPreviewClearRequested()
    {
        DispatcherQueue.TryEnqueue(() => Preview.ShowEmpty());
    }

    private async void OnPreviewRelativeLink(string href)
    {
        if (string.IsNullOrEmpty(ViewModel.OpenedFolderPath) || ViewModel.SelectedItem is null) return;
        var classification = ViewModel.LinkResolver.Classify(
            ViewModel.OpenedFolderPath, ViewModel.SelectedItem.FullPath, href);
        if (classification.Kind == LinkKind.RelativeMarkdown && classification.ResolvedFullPath is { } target)
        {
            await ViewModel.SelectAndLoadAsync(target);
        }
    }

    private async void OnPreviewExternalLink(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try
            {
                var launcher = _scopeProvider?.GetService<IExternalUriLauncher>();
                if (launcher is not null)
                {
                    await launcher.LaunchAsync(uri);
                }
            }
            catch { /* best effort */ }
        }
    }

    private void OnPreviewSearchResult(int total, int currentOneBased)
    {
        if (total == 0)
        {
            SearchStatus.Text = string.IsNullOrEmpty(SearchBox.Text) ? "" : "No results";
        }
        else
        {
            SearchStatus.Text = $"{currentOneBased} / {total}";
        }
    }

    // ----- WebView2-forwarded keyboard shortcuts -----
    private void OnPreviewShortcut(string id)
    {
        var args = new RoutedEventArgs();
        switch (id)
        {
            case "open-folder":             OnOpenFolderClick(this, args);            break;
            case "close-window":            OnCloseWindowClick(this, args);           break;
            case "new-window":              OnNewWindowClick(this, args);             break;
            case "minimize":                OnMinimizeClick(this, args);              break;
            case "toggle-sidebar":          OnToggleSidebarClick(this, args);         break;
            case "find":                    OnFindClick(this, args);                  break;
            case "find-next":               OnFindNextClick(this, args);              break;
            case "find-prev":               OnFindPrevClick(this, args);              break;
            case "use-selection-for-find":  OnUseSelectionForFindClick(this, args);   break;
            case "zoom-in":                 OnZoomInClick(this, args);                break;
            case "zoom-out":                OnZoomOutClick(this, args);               break;
            case "zoom-reset":              OnZoomResetClick(this, args);             break;
            case "select-all":              OnSelectAllClick(this, args);             break;
            case "copy":                    OnCopyClick(this, args);                  break;
        }
    }

    private const int VK_OEM_PLUS  = 0xBB;
    private const int VK_OEM_MINUS = 0xBD;

    private async void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;

        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!ctrl) return;

        var menu = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (menu) return;

        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox) return;

        var key = (int)e.Key;
        if (key == VK_OEM_PLUS)
        {
            await SetZoomAsync(Math.Min(3.0, ViewModel.Settings.Current.ZoomFactor + 0.1));
            e.Handled = true;
        }
        else if (key == VK_OEM_MINUS)
        {
            await SetZoomAsync(Math.Max(0.5, ViewModel.Settings.Current.ZoomFactor - 0.1));
            e.Handled = true;
        }
    }

    // ----- Search bar -----

    private void OnFindClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is null) return;
        SearchBar.Visibility = Visibility.Visible;
        SearchCaseSensitive.IsChecked = ViewModel.Settings.Current.SearchCaseSensitive;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            Preview.Search(SearchBox.Text, ViewModel.Settings.Current.SearchCaseSensitive);
        }
    }

    private void OnCloseSearchClick(object? sender, RoutedEventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
        Preview.SearchClear();
        SearchStatus.Text = "";
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        Preview.Search(SearchBox.Text, ViewModel.Settings.Current.SearchCaseSensitive);
    }

    private void OnSearchKeyDown(object? sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            if (shift) Preview.SearchPrev();
            else Preview.SearchNext();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            OnCloseSearchClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void OnCaseToggleChanged(object? sender, RoutedEventArgs e)
    {
        var on = SearchCaseSensitive.IsChecked == true;
        ViewModel.Settings.Current.SearchCaseSensitive = on;
        await ViewModel.Settings.SaveAsync();
        if (SearchBar.Visibility == Visibility.Visible)
        {
            Preview.Search(SearchBox.Text, on);
        }
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility != Visibility.Visible) OnFindClick(sender, e);
        Preview.SearchNext();
    }

    private void OnFindPrevClick(object? sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility != Visibility.Visible) OnFindClick(sender, e);
        Preview.SearchPrev();
    }

    private async void OnUseSelectionForFindClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is null) return;

        var selection = await Preview.GetSelectedTextAsync();

        SearchBar.Visibility = Visibility.Visible;
        SearchCaseSensitive.IsChecked = ViewModel.Settings.Current.SearchCaseSensitive;

        if (!string.IsNullOrEmpty(selection))
        {
            var trimmed = selection.Trim();
            SearchBox.Text = trimmed;
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    // ----- View: sidebar / zoom / theme -----

    private async void OnToggleSidebarClick(object? sender, RoutedEventArgs e)
    {
        var visible = Sidebar.Visibility == Visibility.Visible;
        if (visible)
        {
            ViewModel.Settings.Current.SidebarWidth = Math.Max(180, ActiveSidebarWidth);
            Sidebar.Visibility = Visibility.Collapsed;
            SetActiveSidebarWidth(new GridLength(0));
            ViewModel.Settings.Current.SidebarVisible = false;
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            SetActiveSidebarWidth(new GridLength(Math.Max(180, ViewModel.Settings.Current.SidebarWidth)));
            ViewModel.Settings.Current.SidebarVisible = true;
        }
        await ViewModel.Settings.SaveAsync();
    }

    private async void OnMoveSidebarClick(object? sender, RoutedEventArgs e)
    {
        var current = ViewModel.Settings.Current.SidebarPosition;
        var next = current == SidebarPosition.Left ? SidebarPosition.Right : SidebarPosition.Left;

        var preservedWidth = Math.Max(180, ActiveSidebarWidth);
        var wasVisible = Sidebar.Visibility == Visibility.Visible;

        ViewModel.Settings.Current.SidebarPosition = next;

        ApplySidebarPosition(next);
        if (wasVisible)
        {
            SetActiveSidebarWidth(new GridLength(preservedWidth));
        }

        UpdateMoveSidebarLabel();
        await ViewModel.Settings.SaveAsync();
    }

    private void ApplySidebarPosition(SidebarPosition position)
    {
        if (position == SidebarPosition.Left)
        {
            Grid.SetColumn(Sidebar, 0);
            Grid.SetColumn(SidebarSplitter, 1);
            Grid.SetColumn(ContentArea, 2);
            LeftColumn.Width = new GridLength(Math.Max(180, ViewModel.Settings.Current.SidebarWidth));
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            Grid.SetColumn(ContentArea, 0);
            Grid.SetColumn(SidebarSplitter, 1);
            Grid.SetColumn(Sidebar, 2);
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(Math.Max(180, ViewModel.Settings.Current.SidebarWidth));
        }

        if (Sidebar.Visibility != Visibility.Visible)
        {
            SetActiveSidebarWidth(new GridLength(0));
        }
    }

    private ColumnDefinition ActiveSidebarColumn =>
        ViewModel.Settings.Current.SidebarPosition == SidebarPosition.Left ? LeftColumn : RightColumn;

    private double ActiveSidebarWidth =>
        ActiveSidebarColumn.ActualWidth > 0 ? ActiveSidebarColumn.ActualWidth :
        ActiveSidebarColumn.Width.IsAbsolute ? ActiveSidebarColumn.Width.Value :
        ViewModel.Settings.Current.SidebarWidth;

    private void SetActiveSidebarWidth(GridLength width)
    {
        ActiveSidebarColumn.Width = width;
    }

    private void UpdateMoveSidebarLabel()
    {
        var pos = ViewModel.Settings.Current.SidebarPosition;
        MoveSidebarMenuItem.Text = pos == SidebarPosition.Left
            ? "Move Sidebar to Right"
            : "Move Sidebar to Left";
    }

    // ----- Sidebar splitter (drag to resize) -----

    private bool _splitterDragging;
    private double _splitterStartX;
    private double _splitterStartWidth;
    private uint _splitterPointerId;

    private void OnSplitterPointerEntered(object? sender, PointerRoutedEventArgs e)
    {
        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast); }
        catch { /* best-effort cursor change */ }
    }

    private void OnSplitterPointerExited(object? sender, PointerRoutedEventArgs e)
    {
        if (_splitterDragging) return;
        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow); }
        catch { /* best-effort */ }
    }

    private void OnSplitterPointerPressed(object? sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el) return;
        if (Sidebar.Visibility != Visibility.Visible) return;
        var pp = e.GetCurrentPoint(this);
        _splitterStartX = pp.Position.X;
        _splitterStartWidth = ActiveSidebarWidth;
        _splitterDragging = true;
        _splitterPointerId = e.Pointer.PointerId;
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object? sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        if (e.Pointer.PointerId != _splitterPointerId) return;
        var pp = e.GetCurrentPoint(this);
        var dx = pp.Position.X - _splitterStartX;
        if (ViewModel.Settings.Current.SidebarPosition == SidebarPosition.Right)
        {
            dx = -dx;
        }
        var target = Math.Clamp(_splitterStartWidth + dx, 180, 800);
        SetActiveSidebarWidth(new GridLength(target));
        e.Handled = true;
    }

    private async void OnSplitterPointerReleased(object? sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        if (sender is UIElement el) el.ReleasePointerCapture(e.Pointer);
        _splitterDragging = false;
        try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow); }
        catch { /* best-effort */ }

        var width = Math.Max(180, ActiveSidebarWidth);
        ViewModel.Settings.Current.SidebarWidth = width;
        await ViewModel.Settings.SaveAsync();
        e.Handled = true;
    }

    private async void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        var z = Math.Min(3.0, ViewModel.Settings.Current.ZoomFactor + 0.1);
        await SetZoomAsync(z);
    }

    private async void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        var z = Math.Max(0.5, ViewModel.Settings.Current.ZoomFactor - 0.1);
        await SetZoomAsync(z);
    }

    private async void OnZoomResetClick(object? sender, RoutedEventArgs e)
    {
        await SetZoomAsync(1.0);
    }

    private async Task SetZoomAsync(double factor)
    {
        ViewModel.Settings.Current.ZoomFactor = factor;
        Preview.SetZoom(factor);
        await ViewModel.Settings.SaveAsync();
    }

    private async void OnThemeSystemClick(object? sender, RoutedEventArgs e) => await SetThemeAsync(AppTheme.System);
    private async void OnThemeLightClick(object? sender, RoutedEventArgs e)  => await SetThemeAsync(AppTheme.Light);
    private async void OnThemeDarkClick(object? sender, RoutedEventArgs e)   => await SetThemeAsync(AppTheme.Dark);

    private async Task SetThemeAsync(AppTheme theme)
    {
        ViewModel.Settings.Current.Theme = theme;
        UpdateThemeMenuChecks();
        Preview.SetTheme(ViewModel.EffectiveTheme());
        RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _window?.ApplyTheme(theme);
        await ViewModel.Settings.SaveAsync();
    }

    private void UpdateThemeMenuChecks()
    {
        var t = ViewModel.Settings.Current.Theme;
        ThemeSystemMenu.IsChecked = t == AppTheme.System;
        ThemeLightMenu.IsChecked  = t == AppTheme.Light;
        ThemeDarkMenu.IsChecked   = t == AppTheme.Dark;
    }

    // ----- Window menu -----

    private void OnNewWindowClick(object? sender, RoutedEventArgs e)
    {
        var handle = _windowService?.CreateWindow(initialFolderPath: null, restoreLastFolder: false);
        handle?.Activate();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_window?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
        {
            op.Minimize();
        }
    }

    private void OnWindowZoomClick(object? sender, RoutedEventArgs e)
    {
        if (_window?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
        {
            if (op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            {
                op.Restore();
            }
            else
            {
                op.Maximize();
            }
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        Preview.CopySelection();
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        Preview.SelectAll();
    }

    private void OnBringAllToFrontClick(object? sender, RoutedEventArgs e)
    {
        if (_windowService is null) return;
        foreach (var w in _windowService.Windows)
        {
            try
            {
                if (w is MainWindowHandle mwh)
                {
                    mwh.Window.AppWindow.MoveInZOrderAtTop();
                }
                w.Activate();
            }
            catch { /* best-effort */ }
        }
        _window?.Activate();
    }

    private void OnWindowsChanged() => RebuildWindowMenu();

    /// <summary>
    /// Window メニュー末尾のウィンドウ一覧を再構築する。
    /// セパレーター上 (New Window / Minimize / Bring All) は不変。
    /// </summary>
    private void RebuildWindowMenu()
    {
        if (WindowMenu is null || _windowService is null) return;

        var items = WindowMenu.Items;
        var sepIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] == WindowMenuListSeparator)
            {
                sepIndex = i;
                break;
            }
        }
        if (sepIndex < 0) return;

        while (items.Count > sepIndex + 1)
        {
            items.RemoveAt(items.Count - 1);
        }

        foreach (var w in _windowService.Windows)
        {
            var title = string.IsNullOrEmpty(w.Title) ? "SkimDown" : w.Title;
            var item = new ToggleMenuFlyoutItem { Text = title };
            if (w is MainWindowHandle mwh && mwh.Window == _window)
            {
                item.IsChecked = true;
            }
            var target = w;
            item.Click += (_, _) =>
            {
                try
                {
                    _windowService.ActivateWindow(target);
                }
                catch { /* best-effort */ }
            };
            items.Add(item);
        }
    }
}
