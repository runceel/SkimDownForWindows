using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;
using SkimDownForWindows.Core;
using SkimDownForWindows.Models;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SkimDownForWindows;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow() : this(null, restoreLastFolder: true) { }

    public MainWindow(string? initialFolderPath, bool restoreLastFolder)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Pass the window reference + initial-folder intent to the page so it
        // doesn't have to reach for any static "current window" singleton.
        var startArgs = new MainPageStartArgs(this, initialFolderPath, restoreLastFolder);
        RootFrame.Navigate(typeof(MainPage), startArgs);
    }

    /// <summary>
    /// Update the visible window/title-bar title. Called by the page when the
    /// open folder changes (per SPEC: <c>"FolderName — SkimDown"</c>).
    /// </summary>
    public void SetTitle(string title)
    {
        Title = title;
        AppTitleBar.Title = title;
        WindowManager.NotifyTitleChanged();
    }

    /// <summary>
    /// Apply the user-chosen theme to the entire window — including the
    /// custom <c>TitleBar</c> control that lives outside the hosted Page —
    /// and update the system caption buttons (min/max/close glyphs) so they
    /// remain visible against the new background.
    /// </summary>
    public void ApplyTheme(AppTheme theme)
    {
        // 1. Root grid -> cascades to TitleBar and any non-Page chrome.
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // 2. Caption (min/max/close) button colors. These are drawn by the OS
        //    via AppWindowTitleBar — they don't inherit our XAML theme — so
        //    we have to pick foreground colors explicitly for the active
        //    effective theme.
        var effective = theme;
        if (effective == AppTheme.System)
        {
            // Sample current OS theme so we pick matching caption colors.
            try
            {
                var ui = new Windows.UI.ViewManagement.UISettings();
                var bg = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                effective = (bg.R + bg.G + bg.B) < 384 ? AppTheme.Dark : AppTheme.Light;
            }
            catch { effective = AppTheme.Light; }
        }

        var captionBar = AppWindow?.TitleBar;
        if (captionBar is null) return;

        // Make the caption area itself transparent so MicaBackdrop +
        // RootGrid.Background show through and follow the XAML theme.
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
/// Bundle of values handed to the new <see cref="MainPage"/> via
/// <see cref="Microsoft.UI.Xaml.Controls.Frame.Navigate(System.Type, object)"/>.
/// </summary>
public sealed record MainPageStartArgs(MainWindow Window, string? InitialFolderPath, bool RestoreLastFolder);

