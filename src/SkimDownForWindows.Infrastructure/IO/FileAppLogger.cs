using System;
using System.IO;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// クラッシュログ・診断情報をローカルファイルに追記する軽量ロガー。
/// 旧 <c>LogToFile</c> ヘルパー (App.xaml.cs / MainPage.xaml.cs / MarkdownPreview.xaml.cs) を統合する。
/// </summary>
public sealed class FileAppLogger : IAppLogger
{
    private readonly string _logPath;
    private readonly object _gate = new();

    public FileAppLogger(string? logDirectoryOverride = null, string? fileName = null)
    {
        var dir = logDirectoryOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try { Directory.CreateDirectory(dir); }
        catch { /* best-effort */ }
        _logPath = Path.Combine(dir, fileName ?? "SkimDownForWindows-crash.log");
    }

    public void LogInformation(string message) => Append("INFO", message, null);

    public void LogWarning(string message) => Append("WARN", message, null);

    public void LogError(string message, Exception? exception = null) => Append("ERROR", message, exception);

    private void Append(string level, string message, Exception? exception)
    {
        try
        {
            var line = exception is null
                ? $"[{DateTimeOffset.Now:O}] {level} {message}{Environment.NewLine}"
                : $"[{DateTimeOffset.Now:O}] {level} {message}{Environment.NewLine}{exception}{Environment.NewLine}";
            lock (_gate)
            {
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // ロガー実装は決して例外を投げない (上位の except handler から呼ばれることがあるため)
        }
    }
}
