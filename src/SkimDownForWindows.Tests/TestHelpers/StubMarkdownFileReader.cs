using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// パス → 文字列辞書で <see cref="ReadAsync"/> 結果を返す <see cref="IMarkdownFileReader"/> 実装。
/// 未登録のパスは "# (default)" を返す。読み取り要求は <see cref="ReadCalls"/> に記録される。
/// </summary>
internal sealed class StubMarkdownFileReader : IMarkdownFileReader
{
    private readonly Dictionary<string, string> _contents;
    private readonly ConcurrentQueue<string> _readCalls = new();

    public StubMarkdownFileReader(IEqualityComparer<string>? comparer = null)
    {
        _contents = new Dictionary<string, string>(comparer ?? StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> ReadCalls => _readCalls;

    public string DefaultContent { get; set; } = "# (default)";

    public void SetContent(string absolutePath, string content) => _contents[absolutePath] = content;

    public Task<string> ReadAsync(string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        _readCalls.Enqueue(absoluteFilePath);
        if (_contents.TryGetValue(absoluteFilePath, out var v))
        {
            return Task.FromResult(v);
        }
        return Task.FromResult(DefaultContent);
    }
}
