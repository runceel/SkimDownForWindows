using System;
using System.Collections.Generic;
using System.IO;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// 実ファイルシステムを <see cref="IFileSystem"/> として提供するテスト用ヘルパー。
/// 本番の <c>LocalFileSystem</c> と挙動を揃えるため、例外はすべて吸収する。
///
/// Tests プロジェクトはプラットフォーム依存を避けるため <c>net10.0</c> ターゲット。
/// したがって Infrastructure の <c>LocalFileSystem</c> を直接参照せず、ここで同等実装を持つ。
/// </summary>
internal sealed class RealFileSystem : IFileSystem
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
