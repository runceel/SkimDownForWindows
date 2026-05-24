using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// <see cref="AppSettings"/> をアプリのローカルデータフォルダーに JSON として永続化する既定実装。
/// 同時書き込みは <see cref="SemaphoreSlim"/> で single-flight、書き込みは tmp + atomic move。
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _current = new();

    /// <summary>テスト用に保存先フォルダーを差し替えられる。</summary>
    public JsonSettingsRepository(string? settingsFolderOverride = null)
    {
        var folder = settingsFolderOverride ?? GetDefaultFolder();
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, FileName);
    }

    public AppSettings Current => _current;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, Json);
                if (parsed is not null)
                {
                    _current = parsed;
                }
            }
        }
        catch
        {
            _current = new AppSettings();
        }
        return _current;
    }

    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_current, Json);
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void FlushSync()
    {
        _saveGate.Wait();
        try
        {
            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_current, Json);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static string GetDefaultFolder()
    {
        // パッケージ実行時は LocalFolder、それ以外は %LOCALAPPDATA% にフォールバック。
        try
        {
            var local = global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            return local;
        }
        catch
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SkimDownForWindows");
        }
    }
}
