using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Models;

/// <summary>
/// <see cref="AppTheme"/> 用の <see cref="JsonConverter{T}"/>。
///
/// 旧フォーマットの整数 (<c>0/1/2/3</c>) と、大文字小文字混在の文字列 (<c>"System"</c>) を寛容に読む一方、
/// 書き出しは小文字統一の文字列 (<c>"system"|"light"|"dark"|"custom"</c>) で行う。
///
/// 既存 <c>settings.json</c> が壊れた整数値や未知の文字列を持っていた場合は <see cref="AppTheme.System"/> にフォールバック。
/// </summary>
public sealed class AppThemeJsonConverter : JsonConverter<AppTheme>
{
    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                {
                    if (reader.TryGetInt32(out var intValue) && Enum.IsDefined(typeof(AppTheme), intValue))
                    {
                        return (AppTheme)intValue;
                    }
                    return AppTheme.System;
                }
            case JsonTokenType.String:
                {
                    var raw = reader.GetString();
                    return ParseString(raw);
                }
            case JsonTokenType.Null:
                return AppTheme.System;
            default:
                // 想定外トークンは緩く無視して既定にフォールバック。
                reader.Skip();
                return AppTheme.System;
        }
    }

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ToString(value));
    }

    /// <summary>テストや永続化外利用のための静的パーサ。</summary>
    public static AppTheme ParseString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AppTheme.System;
        }

        // 数値文字列も受理 (旧フォーマット混在を想定)。
        if (int.TryParse(raw, out var intValue) && Enum.IsDefined(typeof(AppTheme), intValue))
        {
            return (AppTheme)intValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "system" => AppTheme.System,
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            "custom" => AppTheme.Custom,
            _ => AppTheme.System,
        };
    }

    /// <summary>テストや永続化外利用のための静的シリアライザ。</summary>
    public static string ToString(AppTheme value) => value switch
    {
        AppTheme.System => "system",
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        AppTheme.Custom => "custom",
        _ => "system",
    };
}
