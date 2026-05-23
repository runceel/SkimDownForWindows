using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Input;
using SkimDownForWindows.Core;
using SkimDownForWindows.Models;
using SkimDownForWindows.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace SkimDownForWindows;

public sealed partial class MainPage : Page
{
    private static SettingsStore? _sharedSettings;
    private FolderWatcher? _watcher;
    private MainWindow? _window;
    private string? _initialFolderPath;
    private bool _restoreLastFolder = true;
    private bool _windowsChangedSubscribed;

    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        try
        {
            _sharedSettings ??= new SettingsStore();
            _sharedSettings.Load();

            _watcher = new FolderWatcher(App.DispatcherQueue);
            ViewModel = new MainPageViewModel(_sharedSettings, _watcher);

            InitializeComponent();
            DataContext = ViewModel;

            ViewModel.PreviewLoadRequested += OnPreviewLoadRequested;
            ViewModel.PreviewClearRequested += OnPreviewClearRequested;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            Preview.RelativeMarkdownLinkClicked += OnPreviewRelativeLink;
            Preview.ExternalLinkClicked += OnPreviewExternalLink;
            Preview.SearchResult += OnPreviewSearchResult;
            Preview.ShortcutInvoked += OnPreviewShortcut;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            DragOver += OnDragOver;
            Drop += OnDrop;

            // Use AddHandler with handledEventsToo:true so KeyDown reaches us
            // even after a child control marked the routed event handled. We
            // rely on this for main-row Ctrl+= / Ctrl+- zoom because those
            // OEM key VirtualKeys aren't named in Windows.System.VirtualKey,
            // which makes them awkward to bind as XAML KeyboardAccelerators.
            AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), handledEventsToo: true);

            UpdateContentVisibility();
            UpdateThemeMenuChecks();
            UpdateMoveSidebarLabel();
        }
        catch (Exception ex)
        {
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
            _initialFolderPath = args.InitialFolderPath;
            _restoreLastFolder = args.RestoreLastFolder;
        }
    }

    /// <summary>
    /// Allow <see cref="WindowManager"/> to flush the shared settings store on
    /// last-window close so the most recent theme / sidebar / per-folder state
    /// makes it to disk before the process exits.
    /// </summary>
    internal static void FlushSharedSettings() => _sharedSettings?.FlushSync();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialize WebView2 with the bundled web folder.
        var appWeb = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        await Preview.InitializeAsync(appWeb);

        var settings = ViewModel.Settings.Current;

        // Apply persisted sidebar width / visibility / position BEFORE deciding
        // which folder to open (so the user never sees the wrong layout flash).
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

        // Apply persisted theme to the Page (MenuBar / TreeView) AND the
        // window (TitleBar + system caption buttons). Without this on cold
        // start, the TitleBar keeps Mica's auto theme and the caption buttons
        // can flip to colors that don't read against the page background.
        RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _window?.ApplyTheme(settings.Theme);
        Preview.SetTheme(ViewModel.EffectiveTheme());

        // Decide which folder this window opens, exactly once:
        //   1. Explicit initial folder (e.g. dropped onto a new window) wins.
        //   2. Otherwise restore LastFolderPath when allowed.
        //   3. Otherwise start in the empty state.
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

        if (!_windowsChangedSubscribed)
        {
            WindowManager.WindowsChanged += OnWindowsChanged;
            _windowsChangedSubscribed = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviewLoadRequested -= OnPreviewLoadRequested;
        ViewModel.PreviewClearRequested -= OnPreviewClearRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Preview.RelativeMarkdownLinkClicked -= OnPreviewRelativeLink;
        Preview.ExternalLinkClicked -= OnPreviewExternalLink;
        Preview.SearchResult -= OnPreviewSearchResult;
        Preview.ShortcutInvoked -= OnPreviewShortcut;
        if (_windowsChangedSubscribed)
        {
            WindowManager.WindowsChanged -= OnWindowsChanged;
            _windowsChangedSubscribed = false;
        }
        _watcher?.Dispose();
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
        // Preview WebView2 stays Visible at all times so its CoreWebView2 can
        // initialize promptly. The empty / no-md overlays sit on top with their
        // own background.
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
            // Repopulate via VM.
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
            System.Diagnostics.Debug.WriteLine($"OpenFolder failed: {ex}");
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
                // If user dropped a file, open its parent.
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file is not null)
                {
                    var parent = Path.GetDirectoryName(file.Path);
                    if (!string.IsNullOrEmpty(parent)) folderPath = parent;
                }
            }
            if (string.IsNullOrEmpty(folderPath)) return;

            // SPEC: dropping onto a window that already has a folder opens a
            // new window. An empty window opens the folder in place.
            if (ViewModel.HasFolder)
            {
                WindowManager.OpenFolderInNewWindow(folderPath);
            }
            else
            {
                await ViewModel.OpenFolderAsync(folderPath);
                BuildRecentFoldersMenu();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Drop failed: {ex}");
        }
    }

    // ----- Preview events -----

    private void OnPreviewLoadRequested(LoadRequest req)
    {
        // Event was raised from a Task continuation that may have run on a
        // thread-pool thread; CoreWebView2 must be touched from the UI thread.
        App.DispatcherQueue.TryEnqueue(() =>
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
        App.DispatcherQueue.TryEnqueue(() => Preview.ShowEmpty());
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
        // RelativeNonMarkdown, OutOfFolder, Blocked: silently ignore (SPEC).
    }

    private async void OnPreviewExternalLink(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try { await Launcher.LaunchUriAsync(uri); }
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
    //
    // When the WebView2's child HWND has focus, WinUI's KeyboardAccelerator
    // never sees the keystroke. The renderer (renderer.js) detects the combo,
    // preventDefaults the browser action, and posts the id back to the host
    // via the "shortcut" web message. We dispatch to the same handlers the
    // menu items invoke so the user-visible behavior is identical regardless
    // of which surface currently has focus.
    private void OnPreviewShortcut(string id)
    {
        // Whitelist + dispatch. Unknown ids are silently dropped — never
        // invoke arbitrary actions from web messages.
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
            // Anything else: ignore.
        }
    }

    // Main-row Ctrl+=, Ctrl++, Ctrl+- support for WinUI focus (sidebar /
    // empty state). The XAML accelerators only cover the numpad's Add /
    // Subtract VirtualKeys, which leaves laptop / TKL users without a way
    // to zoom unless they switch focus into the WebView2 first (where the
    // renderer.js handler picks them up).
    //
    // OemPlus / OemMinus aren't named in Windows.System.VirtualKey, so we
    // compare against the raw VK codes (0xBB / 0xBD).
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
        if (menu) return; // Alt held — leave for menu mnemonics.

        // Skip if the user is typing in a real text input (SearchBox, etc.).
        // FocusManager is more reliable than e.OriginalSource because routed
        // events surface inner template parts (e.g. RootGrid TextBox child).
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

    // Edit > Use Selection for Find (Ctrl+E)
    private async void OnUseSelectionForFindClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is null) return;

        var selection = await Preview.GetSelectedTextAsync();

        SearchBar.Visibility = Visibility.Visible;
        SearchCaseSensitive.IsChecked = ViewModel.Settings.Current.SearchCaseSensitive;

        if (!string.IsNullOrEmpty(selection))
        {
            // Trim multi-line selections to a single line — WebView2 includes
            // hidden whitespace in some browsers and SPEC's behavior is to use
            // the selection verbatim, so we only trim leading/trailing
            // whitespace, not internal whitespace.
            var trimmed = selection.Trim();
            SearchBox.Text = trimmed;
            // Setting Text triggers OnSearchTextChanged which runs the search,
            // so no need to call Preview.Search explicitly here.
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

        // Capture the current sidebar width BEFORE we mutate Settings, because
        // ActiveSidebarColumn/Width derive the active column from
        // Settings.SidebarPosition.
        var preservedWidth = Math.Max(180, ActiveSidebarWidth);
        var wasVisible = Sidebar.Visibility == Visibility.Visible;

        // Update the settings position FIRST so ActiveSidebarColumn now
        // resolves to the destination column (otherwise SetActiveSidebarWidth
        // below would re-set the source column, leaving the destination
        // column at whatever ApplySidebarPosition gave it).
        ViewModel.Settings.Current.SidebarPosition = next;

        ApplySidebarPosition(next);
        if (wasVisible)
        {
            SetActiveSidebarWidth(new GridLength(preservedWidth));
        }

        UpdateMoveSidebarLabel();
        await ViewModel.Settings.SaveAsync();
    }

    /// <summary>
    /// Snap the sidebar / splitter / content area into the columns implied by
    /// <paramref name="position"/>. Column widths are also swapped so the
    /// fixed-width side always owns the sidebar.
    /// </summary>
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

        // If the sidebar was hidden, keep the corresponding column collapsed.
        if (Sidebar.Visibility != Visibility.Visible)
        {
            SetActiveSidebarWidth(new GridLength(0));
        }
    }

    /// <summary>
    /// The column that currently owns the sidebar, regardless of which side
    /// it lives on. Used by <see cref="OnToggleSidebarClick"/> so width
    /// preservation works in both layouts.
    /// </summary>
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
    //
    // The XAML splitter is a thin Border. We implement drag-to-resize via
    // pointer capture so the active sidebar column tracks the pointer. Pixel
    // values are clamped to [180, 800]. The width is persisted on release.

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
        // Sidebar on the right side: dragging right shrinks it; flip the delta.
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
        // Apply ElementTheme to this page so MenuBar / TreeView follow the choice.
        RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // Also propagate to the hosting Window so the custom TitleBar (which
        // lives outside the Page in MainWindow.xaml) and the system caption
        // buttons follow the chosen theme.
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
        // New empty window — do not auto-restore the last folder; the user
        // explicitly asked for an empty surface.
        var win = WindowManager.CreateWindow(initialFolderPath: null, restoreLastFolder: false);
        win.Activate();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_window?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
        {
            op.Minimize();
        }
    }

    // Window > Zoom: maximize or restore (matches macOS Window > Zoom semantics).
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

    // ----- Edit > Copy / Select All (route through WebView2 selection) -----
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
        foreach (var w in WindowManager.Windows)
        {
            try
            {
                w.AppWindow.MoveInZOrderAtTop();
                w.Activate();
            }
            catch { /* best-effort */ }
        }
        // Re-activate the current window last so it remains focused.
        _window?.Activate();
    }

    private void OnWindowsChanged() => RebuildWindowMenu();

    /// <summary>
    /// Refresh the dynamic window list at the bottom of the Window menu.
    /// Items above the separator (New Window / Minimize / Bring All) stay put.
    /// </summary>
    private void RebuildWindowMenu()
    {
        if (WindowMenu is null) return;

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

        foreach (var w in WindowManager.Windows)
        {
            var title = string.IsNullOrEmpty(w.Title) ? "SkimDown" : w.Title;
            var item = new ToggleMenuFlyoutItem { Text = title };
            if (w == _window)
            {
                item.IsChecked = true;
            }
            var target = w;
            item.Click += (_, _) =>
            {
                try
                {
                    target.AppWindow.MoveInZOrderAtTop();
                    target.Activate();
                }
                catch { /* best-effort */ }
            };
            items.Add(item);
        }
    }
}

