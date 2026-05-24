using System.Collections.Generic;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Application.Theme;

/// <summary>
/// <see cref="ColorScheme"/> を SkimDown の CSS 変数群へ解決した中間表現。
///
/// <see cref="CssVariables"/> は <see cref="ColorMapping.All"/> に従って優先順位順に解決済み、
/// 値はすべて <see cref="ColorValueValidator"/> を通過した安全な CSS 色文字列。
/// </summary>
public sealed class ResolvedTheme
{
    public string Id { get; }
    public string DisplayName { get; }
    public ColorSchemeType Type { get; }
    public bool IsDark => Type.IsDark();

    /// <summary>CSS 変数名 (<c>--skim-*</c>) → 値の不変辞書。</summary>
    public IReadOnlyDictionary<string, string> CssVariables { get; }

    public ResolvedTheme(
        string id,
        string displayName,
        ColorSchemeType type,
        IReadOnlyDictionary<string, string> cssVariables)
    {
        Id = id;
        DisplayName = displayName;
        Type = type;
        CssVariables = cssVariables;
    }

    /// <summary>
    /// <paramref name="scheme"/> から <see cref="ResolvedTheme"/> を生成する。
    ///
    /// <see cref="ColorMapping.All"/> を上から走査し、最初に見つかった安全な値を採用。
    /// 見つからない / 不正値 の場合は <see cref="FallbackPalette"/> から既定値を使う。
    /// </summary>
    public static ResolvedTheme Resolve(ColorScheme scheme)
    {
        var fallback = FallbackPalette.For(scheme.Type.IsDark());
        var result = new Dictionary<string, string>(ColorMapping.All.Count);
        foreach (var entry in ColorMapping.All)
        {
            string? value = null;
            foreach (var key in entry.VsCodeKeys)
            {
                if (scheme.Colors.TryGetValue(key, out var raw))
                {
                    var safe = ColorValueValidator.Normalize(raw);
                    if (safe is not null)
                    {
                        value = safe;
                        break;
                    }
                }
            }

            if (value is null && fallback.TryGetValue(entry.CssVariable, out var fb))
            {
                value = fb;
            }

            if (value is not null)
            {
                result[entry.CssVariable] = value;
            }
        }
        return new ResolvedTheme(scheme.Id, scheme.DisplayName, scheme.Type, result);
    }
}
