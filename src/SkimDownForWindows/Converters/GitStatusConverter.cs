using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.Resources;

namespace SkimDownForWindows.Converters;

/// <summary>Native theme brushes and localized accessibility names for Git decorations.</summary>
public sealed class GitStatusConverter : IValueConverter
{
    private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse();

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value as string ?? "";
        if (parameter as string == "Name")
        {
            var key = status switch { "M" => "Modified", "U" => "Untracked", "A" => "Added", _ => null };
            return key is null ? "" : _strings.GetString("GitChanges/" + key);
        }
        var resource = status switch
        {
            "M" => "SystemFillColorCautionBrush",
            "U" or "A" => "SystemFillColorSuccessBrush",
            _ => "TextFillColorPrimaryBrush",
        };
        return Microsoft.UI.Xaml.Application.Current.Resources[resource];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
