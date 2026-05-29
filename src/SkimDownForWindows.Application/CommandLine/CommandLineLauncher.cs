using System;
using System.IO;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Application.CommandLine;

/// <summary>
/// プロセスのコマンドライン引数や File activation の入力パスを <see cref="InitialActivation"/> に解釈する。
/// macOS upstream の挙動 (<c>skimdown ./mydocs</c> = folder mode、<c>skimdown ./README.md</c> = single-file mode)
/// に揃える。
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
    /// コマンドライン引数から最初の有効な <see cref="InitialActivation"/> を解決する。
    /// </summary>
    /// <param name="args">プロセスの引数。慣例として <c>args[0]</c> は実行ファイルパスなので無視する。</param>
    /// <param name="currentDirectory">相対パス解決用のカレントディレクトリ。</param>
    /// <returns>
    /// 最初に classify が成功した引数の <see cref="InitialActivation"/>、もしくはなければ <c>null</c>。
    /// <c>--help</c> 等のスイッチや空白引数は読み飛ばす。
    /// </returns>
    public InitialActivation? TryResolveActivation(string[] args, string currentDirectory)
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

            var resolved = Classify(arg, currentDirectory);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// 1 個のパス (相対 / 絶対) を <see cref="InitialActivation"/> に分類する。
    /// File activation (Explorer ダブルクリック) で複数ファイルを処理する時もこのメソッドを各パスに対して呼ぶ。
    /// </summary>
    /// <param name="path">対象パス。空文字 / null は <c>null</c> を返す。</param>
    /// <param name="currentDirectory">相対パス解決用のカレントディレクトリ。</param>
    /// <returns>
    /// ディレクトリ → <see cref="OpenFolderActivation"/>、
    /// <c>.md</c> / <c>.markdown</c> ファイル → <see cref="OpenSingleFileActivation"/>、
    /// それ以外 (存在しない / 非 Markdown ファイル / 不正パス) → <c>null</c>。
    /// </returns>
    public InitialActivation? Classify(string path, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, currentDirectory);
        }
        catch
        {
            return null;
        }

        if (_fileSystem.DirectoryExists(full))
        {
            return new OpenFolderActivation(PathHelpers.Canonicalize(full));
        }

        if (_fileSystem.FileExists(full) && PathHelpers.IsMarkdownFile(full))
        {
            return new OpenSingleFileActivation(PathHelpers.Canonicalize(full));
        }

        return null;
    }
}
