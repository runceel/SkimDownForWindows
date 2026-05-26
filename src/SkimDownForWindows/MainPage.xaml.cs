using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Input;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Theme;
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
    private ColorSchemeRegistry? _colorSchemes;
    private IShellService? _shellService;
    private IColorSchemeSource? _colorSchemeSource;
    private bool _windowsChangedSubscribed;
    private bool _themesChangedSubscribed;
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
            _colorSchemes = _scopeProvider.GetRequiredService<ColorSchemeRegistry>();
            _shellService = _scopeProvider.GetRequiredService<IShellService>();
            _colorSchemeSource = _scopeProvider.GetRequiredService<IColorSchemeSource>();

            DataContext = ViewModel;

            ViewModel.PreviewLoadRequested += OnPreviewLoadRequested;
            ViewModel.PreviewClearRequested += OnPreviewClearRequested;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            if (!_themesChangedSubscribed)
            {
                _colorSchemes.ThemesChanged += OnThemesChanged;
                _themesChangedSubscribed = true;
            }

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

        // カスタムテーマレジストリを起動時に必ず一度ロード。
        // その上で「保存されている Custom テーマが消えていた」状態を System に正規化し、
        // 必要なら設定を保存 → 適用する (Reload → Normalize → Save → Apply の順序を明示)。
        if (_colorSchemes is not null)
        {
            _colorSchemes.Reload();
            await NormalizePersistedThemeAsync();
            settings = ViewModel.Settings.Current; // reload may have rewritten Theme/CustomThemeId
            RebuildCustomThemeMenuItems();
        }

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
        ApplyThemeToShell(settings.Theme);
        PushThemeToPreview();

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

        if (_themesChangedSubscribed && _colorSchemes is not null)
        {
            _colorSchemes.ThemesChanged -= OnThemesChanged;
            _themesChangedSubscribed = false;
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
                UpdateContentVisibility();
                break;
            case nameof(MainPageViewModel.SelectedItem):
                UpdateContentVisibility();
                _ = SyncTreeSelectionAsync();
                break;
            case nameof(MainPageViewModel.MarkdownCount):
                UpdateMarkdownCount();
                break;
            case nameof(MainPageViewModel.WindowTitle):
                UpdateWindowTitle();
                break;
        }
    }

    /// <summary>
    /// <see cref="MainPageViewModel.SelectedItem"/> の変更を <see cref="Tree"/> 上の
    /// 選択ハイライトに反映する。WinUI 3 の <see cref="TreeView.SelectedItem"/> や
    /// <see cref="TreeView.SelectedNode"/> への代入では、現状の data-binding TreeView
    /// (DataTemplate root に inner <see cref="TreeViewItem"/> を直書きするパターン) で
    /// 視覚的なハイライトが付かない・<see cref="TreeView.RootNodes"/> 階層に深い
    /// node が populate されないため、visual tree を再帰的に走査して
    /// <see cref="FrameworkElement.DataContext"/> が一致する inner
    /// <see cref="TreeViewItem"/> を見つけ、<see cref="TreeViewItem.IsSelected"/> を
    /// 直接 true にする。<c>SelectionMode="Single"</c> のため、他の選択は TreeView 側で
    /// 自動的に解除される。
    ///
    /// folder ノードのシングルクリックも "選択" 扱いになるため、TwoWay バインドは
    /// 採用せず、VM → TreeView の OneWay 同期だけを行う。
    ///
    /// RootItems の入れ替え直後や Layout pass 前は対応する <see cref="TreeViewItem"/>
    /// がまだ realize されていない。<see cref="Task.Delay(int)"/> で時間を空けながら
    /// 最大 <paramref name="maxAttempts"/> 回まで再試行する。<see cref="MainPageViewModel.SelectedItem"/>
    /// が別の値に更新されていれば古い処理は捨てる (別フォルダーへの切り替えや高速な
    /// 復元連打への防御)。
    /// </summary>
    private async Task SyncTreeSelectionAsync(int maxAttempts = 20, int delayMilliseconds = 50)
    {
        var item = ViewModel.SelectedItem;

        try
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!ReferenceEquals(ViewModel.SelectedItem, item)) return;

                if (item is null)
                {
                    ClearTreeSelection(Tree);
                    return;
                }

                var container = FindTreeViewItemByDataContext(Tree, item);
                if (container is not null)
                {
                    container.IsSelected = true;
                    container.StartBringIntoView();
                    return;
                }

                await Task.Delay(delayMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError("SyncTreeSelectionAsync failed", ex);
        }
    }

    /// <summary>
    /// <paramref name="root"/> 配下の visual tree を再帰的に走査し、
    /// <see cref="FrameworkElement.DataContext"/> が <paramref name="target"/> と
    /// 同一参照の <see cref="TreeViewItem"/> を返す。
    /// </summary>
    private static TreeViewItem? FindTreeViewItemByDataContext(DependencyObject root, MarkdownTreeItem target)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem tvi
                && tvi.DataContext is MarkdownTreeItem dc
                && ReferenceEquals(dc, target))
            {
                return tvi;
            }
            var found = FindTreeViewItemByDataContext(child, target);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// visual tree 内のすべての <see cref="TreeViewItem"/> の選択状態を解除する。
    /// 「現在開いているフォルダーをクリア」のような <see cref="MainPageViewModel.SelectedItem"/>
    /// が null になるケースで使う。
    /// </summary>
    private static void ClearTreeSelection(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem tvi && tvi.IsSelected)
            {
                tvi.IsSelected = false;
            }
            ClearTreeSelection(child);
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
            var (themeKey, isDark, vars) = ResolveActiveThemePayload();
            _ = Preview.LoadAsync(req.Markdown, req.RelativePath, themeKey, isDark, vars);
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

    private async void OnThemeSystemClick(object? sender, RoutedEventArgs e) => await SetThemeAsync(AppTheme.System, null);
    private async void OnThemeLightClick(object? sender, RoutedEventArgs e)  => await SetThemeAsync(AppTheme.Light, null);
    private async void OnThemeDarkClick(object? sender, RoutedEventArgs e)   => await SetThemeAsync(AppTheme.Dark, null);

    private async void OnOpenThemesFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_colorSchemeSource is null || _shellService is null)
        {
            return;
        }
        try
        {
            _colorSchemeSource.EnsureDirectoryExists();
            _shellService.Reveal(_colorSchemeSource.DirectoryPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Open Themes Folder failed: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    private async void OnReloadThemesClick(object? sender, RoutedEventArgs e)
    {
        if (_colorSchemes is null)
        {
            return;
        }
        try
        {
            // Reload→ThemesChanged が他ウィンドウへも伝播し、各 MainPage が NormalizePersistedThemeAsync を実行する。
            _colorSchemes.Reload();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Reload Themes failed: {ex.Message}");
        }
        // 自分自身は ThemesChanged ハンドラ内で正規化する (重複は問題なし)。
        await Task.CompletedTask;
    }

    private void OnCustomThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string id && !string.IsNullOrEmpty(id))
        {
            _ = SetThemeAsync(AppTheme.Custom, id);
        }
    }

    private async void OnThemesChanged()
    {
        // ColorSchemeRegistry はバックグラウンドからも呼ばれ得るため UI スレッドへ marshal。
        if (DispatcherQueue is null)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await NormalizePersistedThemeAsync();
                ApplyThemeToShell(ViewModel.Settings.Current.Theme);
                PushThemeToPreview();
                RebuildCustomThemeMenuItems();
                UpdateThemeMenuChecks();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"OnThemesChanged failed: {ex.Message}");
            }
        });
        await Task.CompletedTask;
    }

    /// <summary>
    /// 設定上のテーマ選択を <see cref="ColorSchemeRegistry"/> の現在状態で正規化し、
    /// 変化があった場合は <c>settings.json</c> に保存する。
    /// </summary>
    private async Task NormalizePersistedThemeAsync()
    {
        if (_colorSchemes is null)
        {
            return;
        }
        var settings = ViewModel.Settings.Current;
        var current = new ThemeSelection(settings.Theme, settings.CustomThemeId);
        var normalized = _colorSchemes.Normalize(current);
        if (normalized.Theme != current.Theme
            || !string.Equals(normalized.CustomThemeId, current.CustomThemeId, StringComparison.Ordinal))
        {
            settings.Theme = normalized.Theme;
            settings.CustomThemeId = normalized.CustomThemeId;
            await ViewModel.Settings.SaveAsync();
        }
    }

    private async Task SetThemeAsync(AppTheme theme, string? customId)
    {
        if (_colorSchemes is null)
        {
            return;
        }
        var normalized = _colorSchemes.Normalize(new ThemeSelection(theme, customId));
        ViewModel.Settings.Current.Theme = normalized.Theme;
        ViewModel.Settings.Current.CustomThemeId = normalized.CustomThemeId;

        ApplyThemeToShell(normalized.Theme);
        PushThemeToPreview();
        UpdateThemeMenuChecks();

        await ViewModel.Settings.SaveAsync();
    }

    /// <summary>
    /// 現在のテーマ選択を「キー名 / isDark / CSS 変数辞書」の 3 つ組みに分解する。
    /// </summary>
    private (string ThemeKey, bool IsDark, IReadOnlyDictionary<string, string>? Vars) ResolveActiveThemePayload()
    {
        var settings = ViewModel.Settings.Current;
        if (settings.Theme == AppTheme.Custom)
        {
            var resolved = _colorSchemes?.Resolve(settings.CustomThemeId);
            if (resolved is not null)
            {
                return ("custom", resolved.IsDark, resolved.CssVariables);
            }
            // フォールバック: 該当テーマが消えていれば light/dark を実効値で。
        }

        var effective = ViewModel.EffectiveTheme(); // "light" | "dark"
        return (effective, effective == "dark", null);
    }

    private void ApplyThemeToShell(AppTheme theme)
    {
        var (themeKey, isDark, _) = ResolveActiveThemePayload();
        // Page / Window の RequestedTheme は二値 (Light/Dark) なので、resolved の isDark に従って決定する。
        ElementTheme elementTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Custom => isDark ? ElementTheme.Dark : ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        RequestedTheme = elementTheme;
        _window?.ApplyTheme(theme, theme == AppTheme.Custom ? isDark : null);
    }

    private void PushThemeToPreview()
    {
        var (themeKey, isDark, vars) = ResolveActiveThemePayload();
        Preview.SetTheme(themeKey, isDark, vars);
    }

    private void RebuildCustomThemeMenuItems()
    {
        // 組み込み 3 種 + separator + 動的 custom items + separator + アクション の構成。
        // ThemeBuiltInSeparator と ThemeActionSeparator の間の動的アイテムをクリアして再構築する。
        var items = ThemeSubmenu.Items;
        var startIndex = items.IndexOf(ThemeBuiltInSeparator) + 1;
        var endIndex = items.IndexOf(ThemeActionSeparator);
        if (startIndex < 0 || endIndex < 0 || endIndex < startIndex)
        {
            return;
        }
        for (var i = endIndex - 1; i >= startIndex; i--)
        {
            items.RemoveAt(i);
        }

        if (_colorSchemes is null)
        {
            ThemeBuiltInSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        var schemes = _colorSchemes.Schemes;
        if (schemes.Count == 0)
        {
            ThemeBuiltInSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        ThemeBuiltInSeparator.Visibility = Visibility.Visible;
        var insertAt = startIndex;
        foreach (var scheme in schemes)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = string.IsNullOrEmpty(scheme.DisplayName) ? scheme.Id : scheme.DisplayName,
                Tag = scheme.Id,
            };
            item.Click += OnCustomThemeClick;
            items.Insert(insertAt++, item);
        }
    }

    private void UpdateThemeMenuChecks()
    {
        var t = ViewModel.Settings.Current.Theme;
        var customId = ViewModel.Settings.Current.CustomThemeId;
        ThemeSystemMenu.IsChecked = t == AppTheme.System;
        ThemeLightMenu.IsChecked  = t == AppTheme.Light;
        ThemeDarkMenu.IsChecked   = t == AppTheme.Dark;

        // 動的アイテムのチェック状態を Tag で同定して更新する。
        var startIndex = ThemeSubmenu.Items.IndexOf(ThemeBuiltInSeparator) + 1;
        var endIndex = ThemeSubmenu.Items.IndexOf(ThemeActionSeparator);
        if (startIndex < 0 || endIndex < 0)
        {
            return;
        }
        for (var i = startIndex; i < endIndex; i++)
        {
            if (ThemeSubmenu.Items[i] is ToggleMenuFlyoutItem toggle)
            {
                var matches = t == AppTheme.Custom
                    && toggle.Tag is string id
                    && !string.IsNullOrEmpty(customId)
                    && string.Equals(id, customId, StringComparison.Ordinal);
                toggle.IsChecked = matches;
            }
        }
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

    // ----- Help menu -----

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var appInfo = _scopeProvider?.GetService<IAppInfoService>();
            var launcher = _scopeProvider?.GetService<IExternalUriLauncher>();
            if (appInfo is null || launcher is null) return;

            var dialog = new AboutDialog(appInfo, launcher)
            {
                XamlRoot = this.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError("About ダイアログの表示に失敗", ex);
        }
    }
}
