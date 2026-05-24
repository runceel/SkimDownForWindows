using System.Threading.Tasks;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// <see cref="AppSettings"/> の永続化抽象。
/// 実装は同時書き込み制御 (single-flight) と atomic write を担保する責務を持つ。
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// 現在ロードされている設定。初期化前に呼んでもデフォルトの <see cref="AppSettings"/> を返す。
    /// </summary>
    AppSettings Current { get; }

    /// <summary>
    /// ディスクから設定をロードし、内部状態を更新する。
    /// 破損時はデフォルト値を保持する。例外は出さない。
    /// </summary>
    AppSettings Load();

    /// <summary>
    /// 現在の設定を非同期に永続化する。失敗してもスローしない (best-effort)。
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// 現在の設定を同期的に永続化する。アプリ終了時のドレイン用途。
    /// </summary>
    void FlushSync();
}
