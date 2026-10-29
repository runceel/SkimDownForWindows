using System;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>App-wide persisted preference and notification shared by all window scopes.</summary>
public sealed class GitChangesPreference(ISettingsRepository settings)
{
    public bool IsVisible => settings.Current.IsGitChangesVisible;
    public event Action? Changed;

    public async Task SetVisibleAsync(bool visible)
    {
        if (IsVisible == visible) return;
        settings.Current.IsGitChangesVisible = visible;
        Changed?.Invoke();
        await settings.SaveAsync();
    }
}
