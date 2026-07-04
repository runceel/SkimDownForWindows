using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Domain;
using Windows.ApplicationModel.Resources;

namespace SkimDownForWindows;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse();

    public AppTheme Theme { get; private set; }
    public string? CustomThemeId { get; private set; }
    public ContentMaxWidth ContentMaxWidth { get; private set; }
    public SidebarPosition SidebarPosition { get; private set; }
    public bool SidebarVisible { get; private set; }
    public bool IsTableOfContentsVisible { get; private set; }
    public bool SearchCaseSensitive { get; private set; }
    public double ZoomFactor { get; private set; }
    public bool OpenContainingFolderOnSingleFileActivation { get; private set; }
    public bool RememberWindowPosition { get; private set; }

    public SettingsDialog(AppSettings current, IReadOnlyList<ColorScheme> customThemes)
    {
        InitializeComponent();

        BuildThemeOptions(current, customThemes);
        SelectComboByTag(ContentWidthComboBox, current.ContentMaxWidth.ToString());
        SelectComboByTag(SidebarPositionComboBox, current.SidebarPosition.ToString());

        SingleFileFolderModeToggle.IsOn = current.OpenContainingFolderOnSingleFileActivation;
        SearchCaseToggle.IsOn = current.SearchCaseSensitive;
        RememberWindowPositionToggle.IsOn = current.RememberWindowPosition;
        SidebarVisibleToggle.IsOn = current.SidebarVisible;
        TableOfContentsVisibleToggle.IsOn = current.IsTableOfContentsVisible;
        ZoomNumberBox.Value = Math.Clamp(current.ZoomFactor, 0.5, 3.0);

        Theme = current.Theme;
        CustomThemeId = current.CustomThemeId;
        ContentMaxWidth = current.ContentMaxWidth;
        SidebarPosition = current.SidebarPosition;
        SidebarVisible = current.SidebarVisible;
        IsTableOfContentsVisible = current.IsTableOfContentsVisible;
        SearchCaseSensitive = current.SearchCaseSensitive;
        ZoomFactor = Math.Clamp(current.ZoomFactor, 0.5, 3.0);
        OpenContainingFolderOnSingleFileActivation = current.OpenContainingFolderOnSingleFileActivation;
        RememberWindowPosition = current.RememberWindowPosition;
    }

    private void BuildThemeOptions(AppSettings current, IReadOnlyList<ColorScheme> customThemes)
    {
        ThemeComboBox.Items.Clear();

        AddThemeOption(_strings.GetString("ThemeSystemMenuItem/Text"), AppTheme.System, null);
        AddThemeOption(_strings.GetString("ThemeLightMenuItem/Text"), AppTheme.Light, null);
        AddThemeOption(_strings.GetString("ThemeDarkMenuItem/Text"), AppTheme.Dark, null);
        foreach (var scheme in customThemes)
        {
            var displayName = string.IsNullOrWhiteSpace(scheme.DisplayName) ? scheme.Id : scheme.DisplayName;
            AddThemeOption(displayName, AppTheme.Custom, scheme.Id);
        }

        var selected = false;
        foreach (var item in ThemeComboBox.Items)
        {
            if (item is ComboBoxItem combo && combo.Tag is ThemeOption option)
            {
                if (option.Theme == current.Theme &&
                    string.Equals(option.CustomThemeId, current.CustomThemeId, StringComparison.Ordinal))
                {
                    ThemeComboBox.SelectedItem = combo;
                    selected = true;
                    break;
                }
            }
        }

        if (!selected)
        {
            ThemeComboBox.SelectedIndex = 0;
        }
    }

    private void AddThemeOption(string text, AppTheme theme, string? customThemeId)
    {
        var item = new ComboBoxItem
        {
            Content = text,
            Tag = new ThemeOption(theme, customThemeId),
        };
        ThemeComboBox.Items.Add(item);
    }

    private static void SelectComboByTag(ComboBox combo, string tagValue)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboItem &&
                comboItem.Tag is string tag &&
                string.Equals(tag, tagValue, StringComparison.Ordinal))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: ThemeOption themeOption })
        {
            args.Cancel = true;
            return;
        }
        if (ContentWidthComboBox.SelectedItem is not ComboBoxItem { Tag: string contentWidthRaw } ||
            !Enum.TryParse<ContentMaxWidth>(contentWidthRaw, out var contentWidth))
        {
            args.Cancel = true;
            return;
        }
        if (SidebarPositionComboBox.SelectedItem is not ComboBoxItem { Tag: string sidebarPositionRaw } ||
            !Enum.TryParse<SidebarPosition>(sidebarPositionRaw, out var sidebarPosition))
        {
            args.Cancel = true;
            return;
        }

        Theme = themeOption.Theme;
        CustomThemeId = themeOption.CustomThemeId;
        ContentMaxWidth = contentWidth;
        SidebarPosition = sidebarPosition;
        SidebarVisible = SidebarVisibleToggle.IsOn;
        IsTableOfContentsVisible = TableOfContentsVisibleToggle.IsOn;
        SearchCaseSensitive = SearchCaseToggle.IsOn;
        ZoomFactor = Math.Clamp(
            double.IsFinite(ZoomNumberBox.Value) ? ZoomNumberBox.Value : 1.0,
            0.5,
            3.0);
        OpenContainingFolderOnSingleFileActivation = SingleFileFolderModeToggle.IsOn;
        RememberWindowPosition = RememberWindowPositionToggle.IsOn;
    }

    private sealed record ThemeOption(AppTheme Theme, string? CustomThemeId);
}
