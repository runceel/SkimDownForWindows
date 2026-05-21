using System.Collections.Generic;

namespace SkimDownForWindows.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum SidebarPosition
{
    Left,
    Right,
}

/// <summary>
/// Global app settings persisted as JSON in <c>LocalFolder</c>.
/// Per-folder state lives in <see cref="FolderState"/> objects.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>WebView2 zoom factor (1.0 = 100%). Range 0.5–3.0.</summary>
    public double ZoomFactor { get; set; } = 1.0;

    public bool SearchCaseSensitive { get; set; } = false;

    public double SidebarWidth { get; set; } = 280;

    public bool SidebarVisible { get; set; } = true;

    /// <summary>Which side of the window the sidebar lives on. Defaults to <see cref="SidebarPosition.Left"/>.</summary>
    public SidebarPosition SidebarPosition { get; set; } = SidebarPosition.Left;

    /// <summary>Most-recently-opened folder paths, most recent first. Max 16 entries.</summary>
    public List<string> RecentFolders { get; set; } = new();

    public string? LastFolderPath { get; set; }

    /// <summary>Per-folder state, keyed by canonical folder path.</summary>
    public Dictionary<string, FolderState> FolderStates { get; set; } = new();
}

/// <summary>
/// Per-folder state: which file was last selected, which sub-folders are expanded.
/// </summary>
public sealed class FolderState
{
    /// <summary>Relative (forward-slash) path of the last selected Markdown file, if any.</summary>
    public string? LastSelectedRelativePath { get; set; }

    /// <summary>Relative (forward-slash) paths of expanded folders.</summary>
    public List<string> ExpandedFolders { get; set; } = new();
}
