using System.Collections.Generic;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// <see cref="IClipboardService"/> 呼び出しを文字列リストとして記録するテスト用実装。
/// </summary>
internal sealed class RecordingClipboardService : IClipboardService
{
    private readonly List<string> _writes = new();

    public IReadOnlyList<string> Writes => _writes;

    public string? LastWrite => _writes.Count == 0 ? null : _writes[^1];

    public void SetText(string text) => _writes.Add(text);
}
