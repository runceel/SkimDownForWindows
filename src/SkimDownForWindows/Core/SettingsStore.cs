using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Models;

namespace SkimDownForWindows.Core;

/// <summary>
/// Persists <see cref="AppSettings"/> as a JSON file inside the app's local data folder.
/// All saves go through a single-flight task to avoid clobbering on rapid updates.
/// </summary>
public sealed class SettingsStore
{
    private const string FileName = "settings.json";
    private const int MaxRecentFolders = 16;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _current = new();

    public SettingsStore(string? settingsFolderOverride = null)
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
            // Corrupt or unreadable file: keep defaults rather than crash.
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
            // Atomic replace.
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence. Avoid throwing into UI on disk hiccups.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void UpdateRecentFolders(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var list = _current.RecentFolders;
        // Remove existing entry (case-insensitive) then push to front.
        list.RemoveAll(p => string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, folderPath);
        if (list.Count > MaxRecentFolders)
        {
            list.RemoveRange(MaxRecentFolders, list.Count - MaxRecentFolders);
        }
        _current.LastFolderPath = folderPath;
    }

    public FolderState GetOrCreateFolderState(string folderPath)
    {
        if (!_current.FolderStates.TryGetValue(folderPath, out var state))
        {
            state = new FolderState();
            _current.FolderStates[folderPath] = state;
        }
        return state;
    }

    private static string GetDefaultFolder()
    {
        // Try packaged ApplicationData first; fall back to %LOCALAPPDATA%.
        try
        {
            var local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            return local;
        }
        catch
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SkimDownForWindows");
        }
    }
}
