using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.Buses.TestSupport;

namespace Solve.Core.Tests.Buses;

/// <summary>
/// 安全跳闸时的设备安全状态钩子：锁存在先、安全报文先于关闭通道、限时不阻塞跳闸、不与其他事务交错
/// </summary>
public sealed class BusSafeStopTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SafeStopTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly byte[] SafeFrame = [0xA5, 0x01];

    private static readonly BusRuntimeOptions Options = new()
    {
        ChannelLockTimeout = TimeSpan.FromMilliseconds(500),
        TransactionTimeout = TimeSpan.FromSeconds(1),
        ShutdownTimeout = TimeSpan.FromSeconds(2),
        FaultCooldown = TimeSpan.FromMilliseconds(50),
        SafeStopTimeout = SafeStopTimeout,
    };

    /// <summary>
    /// 记录调用时的现场：状态、物理通道是否还开着、写入是否成功
    /// </summary>
    private sealed class RecordingHandler : IBusSafeStopHandler
    {
        private int _calls;

        public Func<IBusChannel, CancellationToken, Task>? Behavior { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public List<(BusState State, bool TransportOpen, bool WriteOk)> Seen { get; } = [];

        public Func<(BusState, bool)>? Probe { get; set; }

        public async Task RunAsync(BusId busId, BusDescriptor descriptor, IBusChannel channel, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Behavior is { } behavior)
            {
                await behavior(channel, cancellationToken);
                return;
            }

            var written = await channel.WriteAsync(SafeFrame, cancellationToken);
            var (state, open) = Probe!();
            lock (Seen)
                Seen.Add((state, open, written.IsSuccess));
        }
    }

    private readonly SwitchableTransportFactory _factory = new();
    private readonly RecordingHandler _handler = new();
    private readonly BusRuntime _runtime;
    private readonly SimulatedTransport _sim;

    public BusSafeStopTests()
    {
        var descriptor = new TcpBusDescriptor(BusId.New(), "负载总线", "127.0.0.1", 502)
        {
            Timeouts = new BusTimeouts(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)),
        };
        _runtime = BusRuntime.Create(descriptor, _factory, new TestInterlock(), NullLogger.Instance, Options, safeStopHandler: _handler).Value;
        Assert.True(_factory.TryGet(descriptor.Id, out _sim));
        _handler.Probe = () => (_runtime.State, _sim.IsOpen);
    }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    private async Task ConnectAsync()
    {
        Assert.True(_runtime.Connect().IsSuccess);
        await WaitUntilAsync(() => _runtime.State == BusState.Connected, "Connected");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition())
        {
            if (guard.IsCancellationRequested)
                Assert.Fail($"等待条件超时：{description}");
            await Task.Delay(5);
        }
    }

    // ---------- 顺序 ----------

    [Fact]
    public async Task Trip_FromConnected_SendsSafeStateBeforeClosingChannel()
    {
        await ConnectAsync();

        Assert.True(_runtime.SafetyTrip("操作员急停").IsSuccess);

        // 锁存在先：跳闸返回时状态已是 SafeStop
        Assert.Equal(BusState.SafeStop, _runtime.State);
        await _runtime.SafeStopSettled.WaitAsync(Guard);

        var seen = Assert.Single(_handler.Seen);
        Assert.Equal((BusState.SafeStop, true, true), seen);
        Assert.Equal(SafeFrame, _sim.GetTxHistory()[^1]);
        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");
    }

    [Fact]
    public async Task Trip_WhenNotConnected_DoesNotInvokeHandler()
    {
        Assert.True(_runtime.SafetyTrip("未连接时急停").IsSuccess);

        Assert.True(_runtime.SafeStopSettled.IsCompleted);
        await Task.Delay(100);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task RepeatedTrip_RunsHandlerOnce_ResetAndReconnect_RunsAgain()
    {
        await ConnectAsync();
        _runtime.SafetyTrip("第一次");
        _runtime.SafetyTrip("重复");
        await _runtime.SafeStopSettled.WaitAsync(Guard);
        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");
        Assert.Equal(1, _handler.Calls);

        Assert.True(_runtime.Reset().IsSuccess);
        await ConnectAsync();
        _runtime.SafetyTrip("第二次");
        await _runtime.SafeStopSettled.WaitAsync(Guard);

        Assert.Equal(2, _handler.Calls);
    }

    // ---------- 限时 ----------

    [Fact]
    public async Task HandlerHangs_TripStillClosesChannel_WithinTimeout()
    {
        _handler.Behavior = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        await ConnectAsync();

        var watch = Stopwatch.StartNew();
        _runtime.SafetyTrip("急停");
        await _runtime.SafeStopSettled.WaitAsync(Guard);
        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");

        Assert.InRange(watch.Elapsed, SafeStopTimeout * 0.8, SafeStopTimeout + TimeSpan.FromSeconds(1.5));
        Assert.Equal(BusState.SafeStop, _runtime.State);
    }

    [Fact]
    public async Task HandlerIgnoresCancellation_TripStillClosesChannel_AndChannelBecomesUnusable()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateUse = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Behavior = async (channel, _) =>
        {
            // 不响应取消的处理程序
            await released.Task;
            try
            {
                await channel.WriteAsync(SafeFrame, CancellationToken.None);
                lateUse.TrySetResult(null);
            }
            catch (Exception ex)
            {
                lateUse.TrySetResult(ex);
                throw;
            }
        };
        await ConnectAsync();

        _runtime.SafetyTrip("急停");
        await _runtime.SafeStopSettled.WaitAsync(Guard);
        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");

        // 超时放弃后特许通道已失效：迟到的处理程序不能再触碰通道
        released.SetResult();
        Assert.IsType<InvalidOperationException>(await lateUse.Task.WaitAsync(Guard));
    }

    [Fact]
    public async Task HandlerThrows_ChannelStillClosed()
    {
        _handler.Behavior = (_, _) => throw new InvalidOperationException("处理程序缺陷");
        await ConnectAsync();

        _runtime.SafetyTrip("急停");
        await _runtime.SafeStopSettled.WaitAsync(Guard);

        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");
        Assert.Equal(BusState.SafeStop, _runtime.State);
    }

    // ---------- 并发 ----------

    [Fact]
    public async Task DuringSafeStop_OtherTransactionsAreRejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Behavior = async (channel, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            await channel.WriteAsync(SafeFrame, ct);
        };
        await ConnectAsync();

        _runtime.SafetyTrip("急停");
        await entered.Task.WaitAsync(Guard);

        var rejected = await _runtime.ExecuteAsync<int>(
            async (channel, ct) =>
            {
                await channel.WriteAsync(new byte[] { 0x01 }, ct);
                return Result<int>.Success(1);
            },
            CancellationToken.None);

        Assert.True(rejected.IsFailure);
        Assert.Equal(BusErrors.ChannelLockedCode, rejected.Error.Code);
        release.SetResult();
        await _runtime.SafeStopSettled.WaitAsync(Guard);
        Assert.DoesNotContain(_sim.GetTxHistory(), frame => frame is [0x01]);
        Assert.Equal(SafeFrame, _sim.GetTxHistory()[^1]);
    }

    [Fact]
    public async Task TripInsideTransaction_HandlerRunsAfterTransactionReleasesLock()
    {
        await ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.ExecuteAsync<int>(
            (_, _) => throw new InvalidOperationException("驱动缺陷"),
            CancellationToken.None));

        Assert.Equal(BusState.SafeStop, _runtime.State);
        await _runtime.SafeStopSettled.WaitAsync(Guard);
        Assert.Equal((BusState.SafeStop, true, true), Assert.Single(_handler.Seen));
        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");
    }

    [Fact]
    public async Task DisposeRightAfterTrip_StillSendsSafeStateBeforeClosing()
    {
        await ConnectAsync();

        _runtime.SafetyTrip("急停后退出");
        await _runtime.DisposeAsync();

        Assert.Equal((BusState.SafeStop, true, true), Assert.Single(_handler.Seen));
        Assert.True(_runtime.SafeStopSettled.IsCompleted);
        Assert.False(_sim.IsOpen);
    }
}
