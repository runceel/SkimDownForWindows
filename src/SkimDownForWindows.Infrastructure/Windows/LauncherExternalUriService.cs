using System;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;
using Windows.System;

namespace SkimDownForWindows.Infrastructure.Windows;

/// <summary>
/// <see cref="Launcher.LaunchUriAsync"/> を使う <see cref="IExternalUriLauncher"/> 既定実装。
/// </summary>
public sealed class LauncherExternalUriService : IExternalUriLauncher
{
    public async Task LaunchAsync(Uri uri)
    {
        if (uri is null) return;
        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // best-effort
        }
    }
}
