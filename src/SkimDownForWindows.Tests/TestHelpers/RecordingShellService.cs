using System.Collections.Generic;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// <see cref="IShellService"/> 呼び出しをパスのリストとして記録するテスト用実装。
/// </summary>
internal sealed class RecordingShellService : IShellService
{
    private readonly List<string> _revealedPaths = new();

    public IReadOnlyList<string> RevealedPaths => _revealedPaths;

    public string? LastRevealedPath => _revealedPaths.Count == 0 ? null : _revealedPaths[^1];

    public void Reveal(string path) => _revealedPaths.Add(path);
}
