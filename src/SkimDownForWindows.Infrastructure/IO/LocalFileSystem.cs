using System;
using System.Collections.Generic;
using System.IO;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// 実ファイルシステムを <c>System.IO</c> 経由で扱う既定実装。
/// 例外はすべて吸収し、上位の走査ロジックを止めない。
/// </summary>
public sealed class LocalFileSystem : IFileSystem
{
    public bool FileExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    public bool DirectoryExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFileSystemEntries(folderPath);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public bool IsDirectory(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.Directory);
        }
        catch
        {
            return false;
        }
    }

    public bool IsHiddenOrSystem(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System);
        }
        catch
        {
            return false;
        }
    }

    public DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return DateTimeOffset.MinValue;
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }
}
