using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// 実ファイルから Markdown 本文を UTF-8 で読み込む既定実装。
/// 読み込み失敗時はエラーメッセージを Markdown 形式で返し、例外は伝播させない。
/// </summary>
public sealed class LocalMarkdownFileReader : IMarkdownFileReader
{
    public async Task<string> ReadAsync(string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await File.ReadAllTextAsync(absoluteFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"# Read error\n\n```\n{ex.Message}\n```";
        }
    }
}
