using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.Tests.TestHelpers;

internal sealed class RecordingAppLogger : IAppLogger
{
    private readonly List<string> _warnings = [];
    public string[] Warnings { get { lock (_warnings) return _warnings.ToArray(); } }
    public void LogInformation(string message) { }
    public void LogWarning(string message) { lock (_warnings) _warnings.Add(message); }
    public void LogError(string message, Exception? exception = null) => LogWarning(message);
}
