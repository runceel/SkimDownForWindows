using System;
using System.Collections.Generic;
using System.IO;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// <see cref="IColorSchemeSource"/> の既定実装。
///
/// <see cref="SettingsFolderProvider.GetThemesFolder"/> 配下の <c>*.json</c> を読み込む。
/// 読み込みエラーは無視し、空 ID やサブフォルダーは対象外。
/// </summary>
public sealed class LocalColorSchemeSource : IColorSchemeSource
{
    private readonly string _themesFolder;

    public LocalColorSchemeSource(SettingsFolderProvider folderProvider)
    {
        _themesFolder = folderProvider.GetThemesFolder();
    }

    public string DirectoryPath => _themesFolder;

    public void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_themesFolder))
            {
                Directory.CreateDirectory(_themesFolder);
            }
        }
        catch
        {
            // Best-effort: ディレクトリ作成失敗時は単に Load() が空を返す。
        }
    }

    public IReadOnlyList<ColorSchemeJsonEntry> Load()
    {
        EnsureDirectoryExists();
        var result = new List<ColorSchemeJsonEntry>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_themesFolder, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return result;
        }

        foreach (var path in files)
        {
            string id;
            try
            {
                id = Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            result.Add(new ColorSchemeJsonEntry(id, text));
        }

        // ID 昇順 (case-insensitive) で安定ソート。
        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
