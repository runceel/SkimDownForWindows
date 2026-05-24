namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// OS クリップボードに文字列を書き込む抽象。
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// クリップボードに <paramref name="text"/> を書き込む。失敗時もスローしない (best-effort)。
    /// </summary>
    void SetText(string text);
}
