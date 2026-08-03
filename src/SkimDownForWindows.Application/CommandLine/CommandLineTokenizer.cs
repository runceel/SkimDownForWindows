using System;
using System.Collections.Generic;
using System.IO;
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
    /// <item>クォート中の <c>""</c> はリテラルの <c>"</c> を表し、同時にクォート状態を**抜ける**
    /// (MSVC CRT はクォート状態を維持するが、<c>CommandLineToArgvW</c> は抜ける)</item>
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
                    // クォート中の "" はリテラルの " を表し、かつクォート状態を抜ける。
                    // (CommandLineToArgvW の規則。CRT のように inQuotes を維持すると以降の
                    //  空白の扱いが反転してトークン分割がずれる)
                    current.Append('"');
                    i++;
                    inQuotes = false;
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
    /// <item>先頭トークンが実行ファイル (argv[0]) とみなせる場合は落とす。
    /// Windows App SDK は <c>GetCommandLine()</c> の全文を <c>Arguments</c> に入れるため、
    /// 先頭は常にプログラム名トークンになる</item>
    /// <item>空白のみのトークンと <c>-</c> 始まりのスイッチを落とす</item>
    /// </list>
    /// </summary>
    /// <param name="commandLine">分解対象のコマンドライン文字列。</param>
    /// <param name="programNames">
    /// 拡張子なしのプログラム名候補 (例: <c>skim</c> / <c>skimdown</c> / 実行ファイルの stem)。
    /// <c>cmd.exe</c> は argv[0] を入力どおり (<c>skim README.md</c> の <c>skim</c>) 渡すため、
    /// <c>.exe</c> 判定だけでは argv[0] を落としきれない。
    /// </param>
    public static IReadOnlyList<string> ExtractPositionalTargets(
        string? commandLine,
        IReadOnlyCollection<string>? programNames = null)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return Array.Empty<string>();
        }

        var startIndex = IsProgramToken(tokens[0], programNames) ? 1 : 0;

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

    /// <summary>
    /// 先頭トークンが実行ファイル (argv[0]) とみなせるかどうか。
    /// </summary>
    private static bool IsProgramToken(string token, IReadOnlyCollection<string>? programNames)
    {
        if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (programNames is null || programNames.Count == 0)
        {
            return false;
        }

        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(token);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrEmpty(stem))
        {
            return false;
        }

        foreach (var name in programNames)
        {
            if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
