using System;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>
/// UI スレッドへのマーシャリング抽象。WinUI 3 の <c>DispatcherQueue</c> 相当を抽象化する。
///
/// 実装上の制約: WinUI 3 は通常プロセス全体で UI スレッドを 1 つしか持たないため、
/// 既定の実装は Singleton として登録される。将来 multi-dispatcher 構成を採るなら
/// スコープ寿命に再検討すること。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// <paramref name="action"/> を UI スレッドのキューに投入する。投入成功なら <c>true</c>。
    /// </summary>
    bool TryEnqueue(Action action);
}
