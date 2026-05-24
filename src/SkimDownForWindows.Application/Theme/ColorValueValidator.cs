using System;

namespace SkimDownForWindows.Application.Theme;

/// <summary>
/// VS Code カラーテーマで指定された色値が、CSS に安全に注入できるかを判定する純粋関数群。
///
/// 受理する形式:
/// <list type="bullet">
///   <item><c>#rgb</c> / <c>#rgba</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c> (16進カラー)</item>
///   <item><c>rgb(...)</c> / <c>rgba(...)</c> (数字・カンマ・空白・ドット・<c>%</c>)</item>
///   <item><c>hsl(...)</c> / <c>hsla(...)</c> (上記 + 単位識別子 deg/turn/grad/rad)</item>
///   <item>文字列 <c>transparent</c> (case-insensitive)</item>
/// </list>
///
/// 拒否例:
/// <list type="bullet">
///   <item><c>var(--x)</c>, <c>calc(...)</c>, <c>url(...)</c> を含むもの</item>
///   <item><c>;</c>, <c>{</c>, <c>}</c>, <c>&lt;</c>, <c>&gt;</c>, 改行を含むもの (CSS 流出)</item>
///   <item>最大長 (<see cref="MaxLength"/>) を超えるもの</item>
///   <item>CSS Color Level 4 の space-separated 形式 (将来拡張)</item>
/// </list>
/// </summary>
public static class ColorValueValidator
{
    /// <summary>許容される色値の最大長 (文字数)。CSS Level 1-3 の典型的形式に十分な長さ。</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// <paramref name="raw"/> が安全な CSS 色値なら trim 済み文字列を返し、そうでなければ <c>null</c>。
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
        {
            return null;
        }

        foreach (var ch in trimmed)
        {
            if (ch == ';' || ch == '{' || ch == '}' || ch == '<' || ch == '>' || ch == '\\' || ch == '\n' || ch == '\r')
            {
                return null;
            }
        }

        var lower = trimmed.ToLowerInvariant();

        if (lower.Contains("var(") || lower.Contains("calc(") || lower.Contains("url(") || lower.Contains("expression(") || lower.Contains("@import"))
        {
            return null;
        }

        if (lower == "transparent")
        {
            return trimmed;
        }

        if (IsHex(lower))
        {
            return trimmed;
        }

        if (ValidateInner(lower, "rgb(", IsRgbInnerChar) || ValidateInner(lower, "rgba(", IsRgbInnerChar))
        {
            return trimmed;
        }

        if (ValidateInner(lower, "hsl(", IsHslInnerChar) || ValidateInner(lower, "hsla(", IsHslInnerChar))
        {
            return trimmed;
        }

        return null;
    }

    /// <summary><see cref="Normalize"/> の bool 版。</summary>
    public static bool IsSafe(string? raw) => Normalize(raw) is not null;

    private static bool IsHex(string lower)
    {
        if (lower.Length < 4 || lower[0] != '#')
        {
            return false;
        }
        var hex = lower.AsSpan(1);
        if (hex.Length is not (3 or 4 or 6 or 8))
        {
            return false;
        }
        foreach (var ch in hex)
        {
            if (!IsHexDigit(ch))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHexDigit(char ch)
        => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');

    private static bool ValidateInner(string lower, string prefix, Func<char, bool> allowed)
    {
        if (!lower.StartsWith(prefix, StringComparison.Ordinal) || !lower.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }
        var inner = lower.Substring(prefix.Length, lower.Length - prefix.Length - 1);
        if (inner.Length == 0)
        {
            return false;
        }
        foreach (var ch in inner)
        {
            if (!allowed(ch))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsRgbInnerChar(char ch)
        => (ch >= '0' && ch <= '9')
        || ch == ',' || ch == ' ' || ch == '.' || ch == '%'
        || ch == '+' || ch == '-' || ch == '/';

    private static bool IsHslInnerChar(char ch)
        => IsRgbInnerChar(ch)
        || (ch >= 'a' && ch <= 'z');
}
