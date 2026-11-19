namespace Solve.Core.Tests.Buses.TestSupport;

/// <summary>
/// 在第 <c>blockOnCall</c> 次 <see cref="GetUtcNow"/> 调用处挂住调用线程，直到测试 <see cref="Release"/>。
/// </summary>
/// <remarks>
/// BusRuntime 在状态锁内、"状态机已迁移"与"事件入队"之间取事件时间戳。挂住这一次调用，
/// 就能把运行时稳定地停在迁移临界区中间，把只有线程恰好在此被抢占时才出现的竞态变成确定性复现。
/// 等待带看门狗超时：测试中途异常、没来得及放行时，监督任务不会被永久挂起。
/// </remarks>
internal sealed class BlockingTimeProvider(int blockOnCall, TimeSpan watchdog) : TimeProvider, IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    /// <summary>
    /// 被挂住的那次调用已进入等待（调用方此刻仍持有运行时状态锁）。
    /// </summary>
    public Task Blocked => _blocked.Task;

    public void Release() => _release.Set();

    public override DateTimeOffset GetUtcNow()
    {
        if (Interlocked.Increment(ref _calls) == blockOnCall)
        {
            _blocked.TrySetResult();
            if (!_release.Wait(watchdog))
                throw new TimeoutException($"测试未在 {watchdog} 内放行第 {blockOnCall} 次 GetUtcNow 调用。");
        }

        return base.GetUtcNow();
    }

    public void Dispose() => _release.Dispose();
}
