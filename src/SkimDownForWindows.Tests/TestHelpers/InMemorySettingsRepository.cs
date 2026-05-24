using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Tests.TestHelpers;

/// <summary>
/// メモリ上に <see cref="AppSettings"/> を保持する <see cref="ISettingsRepository"/> テスト用実装。
///
/// <para>
/// <see cref="MainPageViewModel"/> のテストでは <c>async void OnTreeMayHaveChanged</c> や
/// <c>_ = SelectAndLoadAsync(...)</c> といった fire-and-forget なハンドラーが
/// 副作用として <see cref="SaveAsync"/> を呼ぶ。テスト側で完了を待てるよう
/// <see cref="WaitForSaveCountAsync"/> を提供する。
/// </para>
/// </summary>
internal sealed class InMemorySettingsRepository : ISettingsRepository
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<int>> _waiters = new();
    private int _saveAsyncCalls;
    private int _flushSyncCalls;

    public InMemorySettingsRepository(AppSettings? initial = null)
    {
        Current = initial ?? new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public int SaveAsyncCalls
    {
        get { lock (_gate) { return _saveAsyncCalls; } }
    }

    public int FlushSyncCalls
    {
        get { lock (_gate) { return _flushSyncCalls; } }
    }

    public AppSettings Load() => Current;

    public Task SaveAsync()
    {
        List<TaskCompletionSource<int>> toFire;
        int count;
        lock (_gate)
        {
            _saveAsyncCalls++;
            count = _saveAsyncCalls;
            toFire = new List<TaskCompletionSource<int>>(_waiters);
            _waiters.Clear();
        }

        // SaveAsync の同期完了をテストから観測できるよう、Task.CompletedTask を返す前に
        // pending な waiter を起こす。
        foreach (var w in toFire)
        {
            w.TrySetResult(count);
        }
        return Task.CompletedTask;
    }

    public void FlushSync()
    {
        lock (_gate) { _flushSyncCalls++; }
    }

    /// <summary>
    /// <see cref="SaveAsync"/> が <paramref name="expectedCount"/> 回以上呼ばれるまで待機する。
    /// 既に達成済みなら即時返る。<paramref name="timeout"/> 超過時は例外を投げる。
    /// </summary>
    public async Task WaitForSaveCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            TaskCompletionSource<int> tcs;
            lock (_gate)
            {
                if (_saveAsyncCalls >= expectedCount)
                {
                    return;
                }
                tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(tcs);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"SaveAsync was called {SaveAsyncCalls} times; expected at least {expectedCount}.");
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(remaining)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                throw new TimeoutException(
                    $"SaveAsync was called {SaveAsyncCalls} times; expected at least {expectedCount}.");
            }
            // Re-check count under lock on the next loop iteration.
        }
    }
}
