using System;
using System.IO;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Application.CommandLine;

/// <summary>
/// プロセスのコマンドライン引数を解釈し、最初のウィンドウが開くべきフォルダーを決定する。
/// macOS upstream の <c>skimdown ./mydocs</c> 挙動と一致させる。
///
/// すべての入力は明示的なパラメーターで受け取るため <see cref="Environment"/> や
/// グローバルな I/O を直接呼ばずに単体テスト可能。
/// </summary>
public sealed class CommandLineLauncher
{
    private readonly IFileSystem _fileSystem;

    public CommandLineLauncher(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// コマンドライン引数からフォルダーを解決する。
    /// </summary>
    /// <param name="args">プロセスの引数。慣例として <c>args[0]</c> は実行ファイルパスなので無視する。</param>
    /// <param name="currentDirectory">相対パス解決用のカレントディレクトリ。</param>
    /// <returns>開くべき絶対フォルダーパス、もしくはなければ <c>null</c>。<c>--help</c> 等のスイッチは無視する。</returns>
    public string? TryGetInitialFolderPath(string[] args, string currentDirectory)
    {
        if (args is null || args.Length < 2)
        {
            return null;
        }

        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }
            if (arg.StartsWith('-'))
            {
                continue;
            }

            var resolved = ResolveFolder(arg, currentDirectory);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private string? ResolveFolder(string arg, string currentDirectory)
    {
        string full;
        try
        {
            full = Path.IsPathFullyQualified(arg)
                ? Path.GetFullPath(arg)
                : Path.GetFullPath(arg, currentDirectory);
        }
        catch
        {
            return null;
        }

        if (_fileSystem.DirectoryExists(full))
        {
            return full;
        }

        if (_fileSystem.FileExists(full) && PathHelpers.IsMarkdownFile(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent) && _fileSystem.DirectoryExists(parent))
            {
                return parent;
            }
        }

        return null;
    }
}
