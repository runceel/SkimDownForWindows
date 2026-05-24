using System;
using Microsoft.UI.Dispatching;
using SkimDownForWindows.Application.Abstractions;

namespace SkimDownForWindows.Composition;

/// <summary>
/// <see cref="DispatcherQueue"/> を用いた <see cref="IUiDispatcher"/> 既定実装。
/// WindowsAppSDK 依存のためプレゼンテーション (App プロジェクト) に配置する。
///
/// 制約: WinUI 3 は通常プロセス全体で UI スレッドを 1 つしか持たないため、本実装は
/// Singleton として登録される。起動直後に <see cref="DispatcherQueue.GetForCurrentThread"/>
/// から取得した参照を保持する。
/// </summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public bool TryEnqueue(Action action)
    {
        if (action is null) return false;
        return _queue.TryEnqueue(() => action());
    }
}
