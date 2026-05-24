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

    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly string _filePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _current = new();

    /// <summary>テスト用に保存先フォルダーを差し替えられる。</summary>
    public JsonSettingsRepository(SettingsFolderProvider? folderProvider = null)
    {
        var folder = (folderProvider ?? new SettingsFolderProvider()).GetSettingsFolder();
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, FileName);
    }

    /// <summary>後方互換: 旧 API (フォルダーパスを直接渡す形) も継続サポート。</summary>
    public JsonSettingsRepository(string settingsFolderOverride)
        : this(new SettingsFolderProvider(settingsFolderOverride)) { }

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
                    parsed.NormalizeAfterLoad();
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

    /// <summary>
    /// <see cref="AppTheme"/> 用カスタムコンバーターを含む <see cref="JsonSerializerOptions"/> を構築する。
    /// 旧フォーマット (整数) との互換性は <see cref="AppThemeJsonConverter"/> 側で吸収する。
    /// </summary>
    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new AppThemeJsonConverter());
        return options;
    }
}

