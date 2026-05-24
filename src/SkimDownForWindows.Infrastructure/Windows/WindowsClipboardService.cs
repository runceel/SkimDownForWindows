using SkimDownForWindows.Application.Abstractions;
using Windows.ApplicationModel.DataTransfer;

namespace SkimDownForWindows.Infrastructure.Windows;

/// <summary>
/// Windows.ApplicationModel.DataTransfer.Clipboard を使う <see cref="IClipboardService"/> 既定実装。
/// </summary>
public sealed class WindowsClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        }
        catch
        {
            // best-effort
        }
    }
}
