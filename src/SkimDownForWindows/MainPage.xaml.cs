using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            DragOver += OnDragOver;
            Drop += OnDrop;

            UpdateContentVisibility();
            UpdateThemeMenuChecks();
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialize WebView2 with the bundled web folder.
        var appWeb = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        await Preview.InitializeAsync(appWeb);

        // Restore last folder (if any).
        var settings = ViewModel.Settings.Current;
        if (!string.IsNullOrEmpty(settings.LastFolderPath) && Directory.Exists(settings.LastFolderPath))
        {
            await ViewModel.OpenFolderAsync(settings.LastFolderPath);
        }

        // Apply persisted sidebar width / visibility.
        if (settings.SidebarVisible)
        {
            SidebarColumn.Width = new GridLength(Math.Max(180, settings.SidebarWidth));
            Sidebar.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
        }

        Preview.SetZoom(settings.ZoomFactor);

        BuildRecentFoldersMenu();
        UpdateMarkdownCount();
        UpdateWindowTitle();
        UpdateContentVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviewLoadRequested -= OnPreviewLoadRequested;
        ViewModel.PreviewClearRequested -= OnPreviewClearRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Preview.RelativeMarkdownLinkClicked -= OnPreviewRelativeLink;
        Preview.ExternalLinkClicked -= OnPreviewExternalLink;
        Preview.SearchResult -= OnPreviewSearchResult;
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
        if (App.Window is MainWindow mw)
        {
            mw.SetTitle(ViewModel.WindowTitle);
        }
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
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
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
        App.Window?.Close();
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
            e.DragUIOverride.Caption = "Open in SkimDown";
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
            if (folder is null)
            {
                // If user dropped a file, open its parent.
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file is not null)
                {
                    var parent = Path.GetDirectoryName(file.Path);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        await ViewModel.OpenFolderAsync(parent);
                        BuildRecentFoldersMenu();
                    }
                }
                return;
            }

            await ViewModel.OpenFolderAsync(folder.Path);
            BuildRecentFoldersMenu();
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

    // ----- View: sidebar / zoom / theme -----

    private async void OnToggleSidebarClick(object? sender, RoutedEventArgs e)
    {
        var visible = Sidebar.Visibility == Visibility.Visible;
        if (visible)
        {
            ViewModel.Settings.Current.SidebarWidth = Math.Max(180, SidebarColumn.ActualWidth);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            ViewModel.Settings.Current.SidebarVisible = false;
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(Math.Max(180, ViewModel.Settings.Current.SidebarWidth));
            ViewModel.Settings.Current.SidebarVisible = true;
        }
        await ViewModel.Settings.SaveAsync();
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
        await ViewModel.Settings.SaveAsync();
    }

    private void UpdateThemeMenuChecks()
    {
        var t = ViewModel.Settings.Current.Theme;
        ThemeSystemMenu.IsChecked = t == AppTheme.System;
        ThemeLightMenu.IsChecked  = t == AppTheme.Light;
        ThemeDarkMenu.IsChecked   = t == AppTheme.Dark;
    }
}
