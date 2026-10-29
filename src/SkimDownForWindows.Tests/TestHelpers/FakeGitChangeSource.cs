using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Tests.TestHelpers;

internal sealed class FakeGitChangeSource : IGitChangeSource
{
    public event Action? RepositoryChanged;
    public GitChanges? Result { get; set; }
    public Func<string, CancellationToken, Task<GitChanges?>>? Read { get; set; }
    public int ReadCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool IsDisposed { get; private set; }

    public Task<GitChanges?> ReadAsync(string root, IReadOnlyList<string> paths,
        string? selected, CancellationToken token)
    {
        ReadCalls++;
        return Read?.Invoke(root, token) ?? Task.FromResult(Result);
    }

    public void RaiseRepositoryChanged() => RepositoryChanged?.Invoke();
    public void Stop() => StopCalls++;
    public void Dispose() => IsDisposed = true;
}
