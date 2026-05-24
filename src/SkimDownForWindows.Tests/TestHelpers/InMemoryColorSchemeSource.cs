using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// テスト用の <see cref="IColorSchemeSource"/>。
///
/// メモリ上に <c>(id, json)</c> のエントリを保持し、テストから自由に追加 / クリアできる。
/// 物理ファイルを作らないため Infrastructure 依存なしで Application 層のテストに使える。
/// </summary>
public sealed class InMemoryColorSchemeSource : IColorSchemeSource
{
    private readonly Dictionary<string, string> _entries = new();

    public string DirectoryPath { get; set; } = Path.Combine(Path.GetTempPath(), "skim-in-memory-themes");

    public bool DirectoryExistsCalled { get; private set; }

    public void EnsureDirectoryExists() => DirectoryExistsCalled = true;

    public void Add(string id, string jsonText) => _entries[id] = jsonText;

    public void Remove(string id) => _entries.Remove(id);

    public void Clear() => _entries.Clear();

    public IReadOnlyList<ColorSchemeJsonEntry> Load()
        => _entries
            .OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ColorSchemeJsonEntry(kv.Key, kv.Value))
            .ToList();
}
