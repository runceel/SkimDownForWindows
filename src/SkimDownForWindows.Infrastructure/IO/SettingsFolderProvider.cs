using System;
using System.IO;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>
/// アプリのローカル保存フォルダーを解決する純粋なファクトリ。
///
/// <c>JsonSettingsRepository</c> と <c>LocalColorSchemeSource</c> が同一の基底フォルダーを共有するために、
/// パス解決ロジックを一箇所に集約する。
///
/// 実行形態:
/// <list type="bullet">
///   <item>パッケージ実行時: <c>Windows.Storage.ApplicationData.Current.LocalFolder.Path</c></item>
///   <item>非パッケージ実行時: <c>%LOCALAPPDATA%\SkimDownForWindows</c></item>
/// </list>
/// </summary>
public sealed class SettingsFolderProvider
{
    private const string AppFolderName = "SkimDownForWindows";
    private const string ThemesSubFolder = "Themes";

    private readonly string _baseFolder;

    public SettingsFolderProvider() : this(ResolveDefaultBaseFolder()) { }

    /// <summary>テスト用に基底フォルダーを差し替えられる。</summary>
    public SettingsFolderProvider(string baseFolder)
    {
        _baseFolder = baseFolder;
    }

    /// <summary>設定 JSON (<c>settings.json</c>) が置かれるフォルダー。</summary>
    public string GetSettingsFolder() => _baseFolder;

    /// <summary>カスタムカラースキーマ JSON を置くフォルダー (<c>&lt;base&gt;/Themes</c>)。</summary>
    public string GetThemesFolder() => Path.Combine(_baseFolder, ThemesSubFolder);

    private static string ResolveDefaultBaseFolder()
    {
        // パッケージ実行時は LocalFolder、それ以外は %LOCALAPPDATA% にフォールバック。
        try
        {
            return global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, AppFolderName);
        }
    }
}
