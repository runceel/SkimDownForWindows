using System;
using System.Threading.Tasks;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// 既定ブラウザなどで外部 URI を開くための抽象。
/// </summary>
public interface IExternalUriLauncher
{
    /// <summary>
    /// <paramref name="uri"/> を OS の既定ハンドラーで開く。
    /// 失敗時もスローしない (best-effort)。
    /// </summary>
    Task LaunchAsync(Uri uri);
}
