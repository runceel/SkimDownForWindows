using System.Diagnostics;
using System.IO;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Infrastructure.Windows;

/// <summary>
/// Windows エクスプローラーを起動して指定パスを表示する <see cref="IShellService"/> 既定実装。
/// パスがディレクトリならそれを開き、ファイルなら親フォルダーを開いてそのファイルを選択する。
/// </summary>
public sealed class ExplorerShellService : IShellService
{
    public void Reveal(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // best-effort
        }
    }
}
