using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SkimDownForWindows.Application.Models;

/// <summary>
/// VS Code 互換のカラーテーマ JSON を表すデータモデル。
///
/// SkimDown は <c>name</c>, <c>type</c>, <c>colors</c> のみを使用する。<c>tokenColors</c> 等の
/// 他のキーは無視する (将来拡張)。
///
/// 参考: <see href="https://code.visualstudio.com/api/references/theme-color"/>
/// </summary>
public sealed class ColorScheme
{
    /// <summary>ファイル名由来の一意な識別子 (例: <c>monokai-dimmed</c>)。</summary>
    public string Id { get; }

    /// <summary>JSON 内の <c>name</c>。空または欠落なら <see cref="Id"/> をフォールバックとして使う。</summary>
    public string DisplayName { get; }

    /// <summary>JSON 内の <c>type</c> (省略時は <see cref="ColorSchemeType.Dark"/>)。</summary>
    public ColorSchemeType Type { get; }

    /// <summary>VS Code 互換の <c>colors</c> 辞書 (キー → 色文字列)。型不一致のエントリは含めない。</summary>
    public IReadOnlyDictionary<string, string> Colors { get; }

    public ColorScheme(string id, string displayName, ColorSchemeType type, IReadOnlyDictionary<string, string> colors)
    {
        Id = id;
        DisplayName = displayName;
        Type = type;
        Colors = colors;
    }

    /// <summary>
    /// JSON テキスト + 識別子から <see cref="ColorScheme"/> を読み込む。
    /// パース失敗・必須情報の欠落時は <c>null</c> を返す (例外は投げない)。
    /// </summary>
    public static ColorScheme? LoadFromJson(string jsonText, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonText);
        }
        catch
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = doc.RootElement;
            string displayName = id;
            if (root.TryGetProperty("name", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String)
            {
                var n = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(n))
                {
                    displayName = n;
                }
            }

            var type = ColorSchemeType.Dark;
            if (root.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String)
            {
                type = ParseType(typeEl.GetString());
            }

            var colors = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("colors", out var colorsEl)
                && colorsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in colorsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            colors[prop.Name] = value;
                        }
                    }
                }
            }

            return new ColorScheme(id, displayName, type, colors);
        }
    }

    private static ColorSchemeType ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ColorSchemeType.Dark;
        }
        return raw.Trim().ToLowerInvariant() switch
        {
            "light" => ColorSchemeType.Light,
            "dark" => ColorSchemeType.Dark,
            "hc-light" => ColorSchemeType.HighContrastLight,
            "hc-black" or "hc-dark" => ColorSchemeType.HighContrastDark,
            _ => ColorSchemeType.Dark,
        };
    }
}

/// <summary>VS Code カラーテーマの <c>type</c> 値。</summary>
public enum ColorSchemeType
{
    Light,
    Dark,
    HighContrastLight,
    HighContrastDark,
}

/// <summary><see cref="ColorSchemeType"/> ヘルパー。</summary>
public static class ColorSchemeTypeExtensions
{
    /// <summary>暗色テーマかどうかを返す。</summary>
    public static bool IsDark(this ColorSchemeType type) => type switch
    {
        ColorSchemeType.Dark or ColorSchemeType.HighContrastDark => true,
        _ => false,
    };
}
