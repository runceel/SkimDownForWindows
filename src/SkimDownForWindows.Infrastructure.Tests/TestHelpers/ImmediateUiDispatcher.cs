using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.Tests.TestHelpers;

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool TryEnqueue(Action callback) { callback(); return true; }
}
