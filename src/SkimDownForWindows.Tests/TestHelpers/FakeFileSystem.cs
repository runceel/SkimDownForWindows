using System;
using System.Collections.Generic;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// メモリ上で動く <see cref="IFileSystem"/> のテスト用 fake。
/// 主に <see cref="SkimDownForWindows.Application.Markdown.RecentMarkdownListBuilder"/> の
/// 決定的な検証で、ファイルごとの更新日時を固定値で与えるために使う
/// (実ファイルシステムのタイムスタンプ精度・衝突に依存しないため)。
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, DateTimeOffset> _lastWrite =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>指定パスをファイルとして登録し、更新日時を設定する。</summary>
    public void AddFile(string path, DateTimeOffset lastWriteUtc)
    {
        _files.Add(path);
        _lastWrite[path] = lastWriteUtc;
    }

    public void AddDirectory(string path) => _directories.Add(path);

    public bool FileExists(string path) => !string.IsNullOrEmpty(path) && _files.Contains(path);

    public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && _directories.Contains(path);

    public IEnumerable<string> EnumerateFileSystemEntries(string folderPath) => Array.Empty<string>();

    public bool IsDirectory(string path) => DirectoryExists(path);

    public bool IsHiddenOrSystem(string path) => false;

    public DateTimeOffset GetLastWriteTimeUtc(string path)
        => _lastWrite.TryGetValue(path, out var ts) ? ts : DateTimeOffset.MinValue;
}
