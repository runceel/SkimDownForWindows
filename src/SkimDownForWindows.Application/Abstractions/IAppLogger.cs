using System;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// 軽量なアプリケーションロガー抽象。
/// 例外時のフォールバック書き出しに使うため、実装側で書き込みエラーをスローしてはならない。
/// </summary>
public interface IAppLogger
{
    void LogInformation(string message);

    void LogWarning(string message);

    void LogError(string message, Exception? exception = null);
}
