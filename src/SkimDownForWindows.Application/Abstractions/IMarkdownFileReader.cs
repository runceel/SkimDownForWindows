using System.Threading;
using System.Threading.Tasks;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// Markdown 本文を UTF-8 で読み込む抽象。
/// 例外を投げず、失敗時はエラー本文 (Markdown 形式) を返す責務を持つ。
/// </summary>
public interface IMarkdownFileReader
{
    /// <summary>
    /// <paramref name="absoluteFilePath"/> を UTF-8 として読み込む。
    /// 読み込みに失敗した場合はエラーメッセージを含む Markdown 文字列を返す。
    /// </summary>
    Task<string> ReadAsync(string absoluteFilePath, CancellationToken cancellationToken = default);
}
