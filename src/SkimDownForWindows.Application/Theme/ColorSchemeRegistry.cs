using System;
using System.Collections.Generic;
using System.Linq;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Domain;

namespace SkimDownForWindows.Application.Theme;

/// <summary>
/// アプリ全体で共有するカスタムテーマレジストリ。
///
/// <see cref="IColorSchemeSource"/> からテーマ JSON を読み、 <see cref="ColorScheme"/> として保持する。
/// <see cref="ResolvedTheme"/> はオンデマンドで解決し、キャッシュする (Reload で破棄)。
///
/// 各操作は UI スレッドから呼ばれる前提で、内部状態は <see cref="lock"/> で軽く保護する。
/// <see cref="ThemesChanged"/> は Reload の最後に発火するが、購読者は UI スレッドへ marshal すること。
///
/// 寿命: アプリ全体で 1 つ (Singleton 登録)。
/// </summary>
public sealed class ColorSchemeRegistry
{
    private readonly IColorSchemeSource _source;
    private readonly object _lock = new();
    private IReadOnlyList<ColorScheme> _schemes = Array.Empty<ColorScheme>();
    private Dictionary<string, ColorScheme> _byId = new(StringComparer.Ordinal);
    private Dictionary<string, ResolvedTheme> _resolvedCache = new(StringComparer.Ordinal);

    public ColorSchemeRegistry(IColorSchemeSource source)
    {
        _source = source;
    }

    /// <summary>
    /// 直近のリロードで読み込まれたテーマ一覧 (DisplayName で昇順、case-insensitive)。
    /// 戻り値は immutable な snapshot。
    /// </summary>
    public IReadOnlyList<ColorScheme> Schemes
    {
        get
        {
            lock (_lock)
            {
                return _schemes;
            }
        }
    }

    /// <summary>テーマ JSON 配置フォルダーのパス。</summary>
    public string DirectoryPath => _source.DirectoryPath;

    /// <summary>
    /// レジストリが (再) ロードされた直後に発火する。
    /// 購読側は UI スレッドへの marshal を必ず自前で行うこと。
    /// </summary>
    public event Action? ThemesChanged;

    /// <summary>
    /// テーマフォルダーから JSON を再読み込みする。失敗ファイルはスキップ。
    /// </summary>
    public void Reload()
    {
        var entries = _source.Load();
        var loaded = new List<ColorScheme>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Id) || !seen.Add(entry.Id))
            {
                continue;
            }
            var scheme = ColorScheme.LoadFromJson(entry.JsonText, entry.Id);
            if (scheme is not null)
            {
                loaded.Add(scheme);
            }
        }
        loaded.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        lock (_lock)
        {
            _schemes = loaded;
            _byId = loaded.ToDictionary(s => s.Id, StringComparer.Ordinal);
            _resolvedCache.Clear();
        }

        ThemesChanged?.Invoke();
    }

    /// <summary><paramref name="id"/> に一致するテーマを返す。無ければ <c>null</c>。</summary>
    public ColorScheme? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        lock (_lock)
        {
            return _byId.TryGetValue(id, out var s) ? s : null;
        }
    }

    /// <summary>
    /// <paramref name="id"/> のテーマを <see cref="ResolvedTheme"/> に解決する。無ければ <c>null</c>。
    /// </summary>
    public ResolvedTheme? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        lock (_lock)
        {
            if (_resolvedCache.TryGetValue(id, out var cached))
            {
                return cached;
            }
            if (!_byId.TryGetValue(id, out var scheme))
            {
                return null;
            }
            var resolved = ResolvedTheme.Resolve(scheme);
            _resolvedCache[id] = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// <see cref="ThemeSelection"/> を現在のレジストリ状態で正規化する。
    /// <see cref="AppTheme.Custom"/> かつ ID が無効な場合は <see cref="ThemeSelection.System"/> に戻す。
    /// </summary>
    public ThemeSelection Normalize(ThemeSelection selection)
    {
        if (selection.Theme != AppTheme.Custom)
        {
            return selection with { CustomThemeId = null };
        }
        if (string.IsNullOrEmpty(selection.CustomThemeId))
        {
            return ThemeSelection.System;
        }
        return Find(selection.CustomThemeId) is null
            ? ThemeSelection.System
            : selection;
    }

    /// <summary>
    /// (<paramref name="theme"/>, <paramref name="customId"/>) のペアを正規化するヘルパー。
    /// </summary>
    public ThemeSelection Normalize(AppTheme theme, string? customId)
        => Normalize(new ThemeSelection(theme, customId));
}
