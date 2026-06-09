using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace SkimDownForWindows.Converters;

/// <summary>
/// <see cref="DateTimeOffset"/>? を表示用の文字列に整形する。更新日順の一覧の詳細行で使う。
/// ローカル時刻に変換し、OS のカルチャに従った短い日付 + 時刻 ("g") にする。
/// <c>null</c> や <see cref="DateTimeOffset.MinValue"/> は空文字。
/// </summary>
public sealed partial class ModifiedTimestampToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset dto || dto == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var culture = string.IsNullOrEmpty(language)
            ? CultureInfo.CurrentCulture
            : CultureInfo.GetCultureInfo(language);

        return dto.ToLocalTime().LocalDateTime.ToString("g", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
