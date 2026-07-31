using System;
using System.Collections.Generic;
using System.Text;

namespace SkimDownForWindows.Application.CommandLine;

/// <summary>
/// Windows のコマンドライン文字列を引数トークンに分解する純粋ヘルパー。
///
/// 単一インスタンス redirect では、二次プロセスのコマンドラインが
/// <c>ILaunchActivatedEventArgs.Arguments</c> という「1 本の文字列」として主プロセスに届く。
/// 主プロセス側で <see cref="Environment.GetCommandLineArgs"/> を読むと自分自身の起動時引数に
/// なってしまうため、この文字列を CRT / <c>CommandLineToArgvW</c> と同じ規則で分解する必要がある。
/// </summary>
public static class CommandLineTokenizer
{
    /// <summary>
    /// コマンドライン文字列を引数トークンに分解する。
    ///
    /// <c>CommandLineToArgvW</c> と同じ規則:
    /// <list type="bullet">
    /// <item>クォートの外側の空白文字 (スペース / タブ) がトークン区切り</item>
    /// <item><c>"</c> でクォート状態がトグルする</item>
    /// <item>バックスラッシュ列は、直後が <c>"</c> の時のみ 2 個で 1 個にエスケープされる</item>
    /// <item>クォート中の <c>""</c> はリテラルの <c>"</c> を表す</item>
    /// </list>
    /// </summary>
    /// <param name="commandLine">分解対象。<c>null</c> / 空白のみなら空リストを返す。</param>
    public static IReadOnlyList<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    // バックスラッシュ 2 個で 1 個のリテラル。奇数個なら最後の 1 個が " をエスケープする。
                    current.Append('\\', backslashes / 2);
                    if ((backslashes % 2) == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        i--; // " は次のループでクォート文字として処理させる
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--; // 直前で読み過ぎた 1 文字を次のループで処理させる
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    // クォート中の "" はリテラルの "
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                hasToken = true;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// コマンドライン文字列から「開く対象になりうる位置引数」だけを取り出す。
    ///
    /// <list type="number">
    /// <item><see cref="Tokenize"/> で分解する</item>
    /// <item>先頭トークンが <c>.exe</c> で終わる場合は実行ファイルパスとみなして落とす
    /// (packaged app の <c>Arguments</c> は argv[0] を含むことがある)</item>
    /// <item>空白のみのトークンと <c>-</c> 始まりのスイッチを落とす</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ExtractPositionalTargets(string? commandLine)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return Array.Empty<string>();
        }

        var startIndex = tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        var result = new List<string>(Math.Max(0, tokens.Count - startIndex));
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            {
                continue;
            }
            result.Add(token);
        }

        return result;
    }
}
