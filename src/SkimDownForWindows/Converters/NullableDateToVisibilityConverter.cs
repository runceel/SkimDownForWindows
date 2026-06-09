using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace SkimDownForWindows.Converters;

/// <summary>
/// <see cref="DateTimeOffset"/>? が値を持つときだけ <see cref="Visibility.Visible"/> を返す。
/// 更新日順の一覧モードの詳細行 (日時 + フォルダー) を、ツリーモードの leaf
/// (<see cref="System.DateTimeOffset"/> 未設定) では隠すために使う。
/// </summary>
public sealed partial class NullableDateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is DateTimeOffset dto && dto != DateTimeOffset.MinValue;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
