using System;
using System.Collections.Generic;
using System.IO;

namespace SkimDownForWindows.Application.CommandLine;

/// <summary>
/// コマンドライン引数に含まれる相対パスを、指定のカレントディレクトリ基準で絶対パスに正規化する純粋ヘルパー。
///
/// 単一インスタンス redirect (<c>AppInstance.RedirectActivationToAsync</c>) はコマンドライン文字列は運ぶが
/// **カレントディレクトリは運ばない**。主プロセス側で相対パスを解決すると主プロセスの cwd が使われてしまうため、
/// 「正しい cwd を持っているプロセス」= redirect を投げる側で先に絶対パス化しておく必要がある。
///
/// DI コンテナ構築より前 (プロセスエントリーポイント) から呼ばれるため、<c>IFileSystem</c> ではなく
/// 存在確認の述語を引数で受け取る。
/// </summary>
public static class CommandLineArgumentNormalizer
{
    /// <summary>
    /// 引数配列のうち「実在するパスを指す位置引数」だけを絶対パスに置換した配列を返す。
    ///
    /// 次のトークンは一切変更しない:
    /// <list type="bullet">
    /// <item>空文字 / 空白のみ</item>
    /// <item><c>-</c> 始まりのスイッチ</item>
    /// <item>絶対パス解決に失敗するもの (不正パス等)</item>
    /// <item><paramref name="pathExists"/> が <c>false</c> を返すもの (存在しないパス / 将来のスイッチ値)</item>
    /// </list>
    /// </summary>
    /// <param name="args">プロセス引数 (<c>args[0]</c> に実行ファイルパスを含まない、<c>Main(string[] args)</c> 形式)。</param>
    /// <param name="currentDirectory">相対パス解決の基準ディレクトリ。</param>
    /// <param name="pathExists">パスが実在するかを返す述語 (ファイル・ディレクトリのどちらでも <c>true</c>)。</param>
    /// <returns>正規化後の新しい配列。<paramref name="args"/> が <c>null</c> の場合は空配列。</returns>
    public static string[] ToAbsolutePaths(
        IReadOnlyList<string>? args,
        string currentDirectory,
        Func<string, bool> pathExists)
    {
        ArgumentNullException.ThrowIfNull(pathExists);

        if (args is null || args.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            result[i] = NormalizeOne(args[i], currentDirectory, pathExists);
        }

        return result;
    }

    /// <summary>
    /// <see cref="ToAbsolutePaths"/> の結果が元の引数と異なるか (= 相対パスの絶対化が発生したか) を判定する。
    /// 呼び出し側が「正規化が必要だったので子プロセスを起動し直す」判断に使う。
    /// </summary>
    public static bool DiffersFrom(IReadOnlyList<string>? original, IReadOnlyList<string>? normalized)
    {
        var originalCount = original?.Count ?? 0;
        var normalizedCount = normalized?.Count ?? 0;
        if (originalCount != normalizedCount)
        {
            return true;
        }

        for (var i = 0; i < originalCount; i++)
        {
            if (!string.Equals(original![i], normalized![i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeOne(string arg, string currentDirectory, Func<string, bool> pathExists)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
        {
            return arg;
        }

        string full;
        try
        {
            full = Path.IsPathFullyQualified(arg)
                ? Path.GetFullPath(arg)
                : Path.GetFullPath(arg, currentDirectory);
        }
        catch
        {
            return arg;
        }

        bool exists;
        try
        {
            exists = pathExists(full);
        }
        catch
        {
            return arg;
        }

        return exists ? full : arg;
    }
}
