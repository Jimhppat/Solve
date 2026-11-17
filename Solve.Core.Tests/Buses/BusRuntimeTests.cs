using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses;

public sealed class BusRuntimeTests : IAsyncDisposable
{
    // 测试看门狗：任何等待都不得无限挂起
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private static readonly byte[] Request = [0x01, 0x03, 0x00, 0x10];

    private sealed class FakeInterlock : IBusSafetyInterlock
    {
        private volatile bool _active;

        public bool Active
        {
            get => _active;
            set => _active = value;
        }

        public Result CanReset(BusId busId)
            => Active ? Result.Failure(BusErrors.InterlockActive("测试联锁")) : Result.Success();
    }

    private readonly SimulatedTransportFactory _factory = new();
    private readonly FakeInterlock _interlock = new();
    private readonly BusRuntime _runtime;
    private readonly SimulatedTransport _sim;

    public BusRuntimeTests()
    {
        var descriptor = new TcpBusDescriptor(BusId.New(), "PLC-01", "127.0.0.1", 502)
        {
            Timeouts = new BusTimeouts(
                Connect: TimeSpan.FromMilliseconds(200),
                Read: TimeSpan.FromMilliseconds(100),
                Write: TimeSpan.FromMilliseconds(100)),
            Reconnect = new ReconnectPolicy(
                InitialDelay: TimeSpan.FromMilliseconds(10),
                MaxDelay: TimeSpan.FromMilliseconds(40),
                Multiplier: 2.0,
                MaxAttempts: 3),
        };

        var options = new BusRuntimeOptions
        {
            ChannelLockTimeout = TimeSpan.FromMilliseconds(500),
            TransactionTimeout = TimeSpan.FromSeconds(1),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            FaultCooldown = TimeSpan.FromMilliseconds(50),
        };

        _runtime = BusRuntime.Create(descriptor, _factory, _interlock, NullLogger.Instance, options).Value;
        Assert.True(_factory.TryGet(descriptor.Id, out _sim));
        _sim.Responder = request => request.ToArray();
    }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    // ---------- 辅助 ----------

    private async Task WaitForStateAsync(BusState expected)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (_runtime.State != expected)
        {
            if (guard.IsCancellationRequested)
                Assert.Fail($"等待状态 {expected} 超时，当前为 {_runtime.State}");

            await Task.Delay(5);
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition())
        {
            if (guard.IsCancellationRequested)
                Assert.Fail($"等待条件超时：{description}");

            await Task.Delay(5);
        }
    }

    private async Task ConnectAsync()
    {
        Assert.True(_runtime.Connect().IsSuccess);
        await WaitForStateAsync(BusState.Connected);
    }

    private List<BusStateChanged> DrainEvents()
    {
        var events = new List<BusStateChanged>();
        while (_runtime.StateChanges.TryRead(out var changed))
            events.Add(changed);
        return events;
    }

    private static async ValueTask<Result<byte[]>> EchoAsync(IBusChannel channel, CancellationToken ct)
    {
        var discard = await channel.DiscardInputAsync(ct);
        if (discard.IsFailure)
            return Result<byte[]>.Failure(discard.Error);

        var write = await channel.WriteAsync(Request, ct);
        if (write.IsFailure)
            return Result<byte[]>.Failure(write.Error);

        var buffer = new byte[64];
        var read = await channel.ReadAsync(buffer, ct);
        return read.IsSuccess
            ? Result<byte[]>.Success(buffer[..read.Value])
            : Result<byte[]>.Failure(read.Error);
    }

    private Task<Result<byte[]>> ExecuteEchoAsync()
        => _runtime.ExecuteAsync(EchoAsync, new CancellationTokenSource(Guard).Token);

    // ---------- 创建与连接 ----------

    [Fact]
    public void Create_InvalidDescriptor_Fails()
    {
        var invalid = new TcpBusDescriptor(BusId.New(), "PLC", "", 0);

        var result = BusRuntime.Create(invalid, new SimulatedTransportFactory(), _interlock, NullLogger.Instance);

        Assert.Equal(BusErrors.InvalidDescriptorCode, result.Error.Code);
    }

    [Fact]
    public void NewRuntime_IsDisconnected_AndDoesNotAutoConnect()
    {
        Assert.Equal(BusState.Disconnected, _runtime.State);
        Assert.False(_sim.IsOpen);
    }

    [Fact]
    public async Task Connect_ReachesConnected_WithOrderedEvents()
    {
        await ConnectAsync();

        Assert.True(_sim.IsOpen);
        Assert.Equal(
            [
                new BusTransition(BusState.Disconnected, BusTrigger.Connect, BusState.Connecting),
                new BusTransition(BusState.Connecting, BusTrigger.OpenSucceeded, BusState.Connected),
            ],
            DrainEvents().Select(e => e.Transition));
    }

    [Fact]
    public async Task OpenFailure_IsRetried_AndRecovers()
    {
        _sim.FailNextOpen();

        await ConnectAsync();

        var reconnect = Assert.Single(DrainEvents(), e => e.Transition.To == BusState.Reconnecting);
        Assert.Equal(BusErrors.OpenFailedCode, reconnect.Cause?.Code);
    }

    [Fact]
    public async Task RetryExhausted_LatchesFaulted_UntilReset()
    {
        // 打开耗时 1 s > 连接超时 200 ms：每次打开都超时
        _sim.OpenDelay = TimeSpan.FromSeconds(1);
        Assert.True(_runtime.Connect().IsSuccess);

        await WaitForStateAsync(BusState.Faulted);

        var events = DrainEvents();
        Assert.Equal(BusErrors.RetryExhaustedCode, events[^1].Cause?.Code);
        Assert.Equal(3, events.Count(e => e.Transition is { From: BusState.Reconnecting, Trigger: BusTrigger.OpenFailed }));
        Assert.False(_sim.IsOpen);

        // 锁存：不能绕过复位直接重连，通道锁定
        Assert.Equal(BusErrors.InvalidTransitionCode, _runtime.Connect().Error.Code);
        Assert.Equal(BusErrors.ChannelLockedCode, (await ExecuteEchoAsync()).Error.Code);

        // Faulted 是通信故障，复位不受安全联锁约束
        _interlock.Active = true;
        _sim.OpenDelay = TimeSpan.Zero;
        Assert.True(_runtime.Reset().IsSuccess);
        await ConnectAsync();
    }

    // ---------- 事务 ----------

    [Fact]
    public async Task Execute_Echo_Succeeds()
    {
        await ConnectAsync();

        var result = await ExecuteEchoAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Request, result.Value);
    }

    [Fact]
    public async Task Execute_WhenDisconnected_ReturnsChannelLocked()
    {
        Assert.Equal(BusErrors.ChannelLockedCode, (await ExecuteEchoAsync()).Error.Code);
        Assert.Empty(_sim.GetTxHistory());
    }

    [Fact]
    public async Task ReadTimeout_ReturnsTimeout_AndStaysConnected()
    {
        _sim.Responder = _ => null;
        await ConnectAsync();

        var result = await ExecuteEchoAsync();

        Assert.Equal(BusErrors.TimeoutCode, result.Error.Code);
        Assert.Equal(BusState.Connected, _runtime.State);
    }

    [Fact]
    public async Task IoFailure_SwitchesToReconnecting_AndRecoversAutomatically()
    {
        await ConnectAsync();
        DrainEvents();
        _sim.FailNextWrite();

        var failed = await ExecuteEchoAsync();

        Assert.Equal(BusErrors.IoFailedCode, failed.Error.Code);
        await WaitForStateAsync(BusState.Connected);

        var lost = Assert.Single(DrainEvents(), e => e.Transition.From == BusState.Connected);
        Assert.Equal(BusTrigger.IoFailed, lost.Transition.Trigger);
        Assert.True((await ExecuteEchoAsync()).IsSuccess);
    }

    [Fact]
    public async Task DroppedConnection_IsDetectedOnNextIo_AndRecovers()
    {
        await ConnectAsync();
        _sim.DropConnection();

        Assert.True((await ExecuteEchoAsync()).IsFailure);

        await WaitForStateAsync(BusState.Connected);
        Assert.True((await ExecuteEchoAsync()).IsSuccess);
    }

    [Fact]
    public async Task Transactions_AreSerialized()
    {
        await ConnectAsync();
        var concurrent = 0;
        var maxConcurrent = 0;

        async ValueTask<Result<int>> Body(IBusChannel channel, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref concurrent);
            return Result<int>.Success(now);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _runtime.ExecuteAsync(Body, CancellationToken.None)));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task ChannelLockWait_IsBounded()
    {
        await ConnectAsync();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _runtime.ExecuteAsync<int>(async (_, ct) =>
        {
            holding.SetResult();
            await Task.Delay(800, ct);
            return Result<int>.Success(0);
        }, CancellationToken.None);

        await holding.Task.WaitAsync(Guard);
        var waiter = await _runtime.ExecuteAsync<int>((_, _) => ValueTask.FromResult(Result<int>.Success(1)), CancellationToken.None);

        Assert.Equal(BusErrors.ChannelBusyCode, waiter.Error.Code);
        Assert.True((await holder.WaitAsync(Guard)).IsSuccess);
    }

    [Fact]
    public async Task TransactionTimeout_IsEnforced()
    {
        await ConnectAsync();

        var result = await _runtime.ExecuteAsync<int>(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<int>.Success(0);
        }, CancellationToken.None).WaitAsync(Guard);

        Assert.Equal(BusErrors.TimeoutCode, result.Error.Code);
        Assert.Equal(BusState.Connected, _runtime.State);
    }

    [Fact]
    public async Task CallerCancellation_IsThrown_NotMapped()
    {
        await ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runtime.ExecuteAsync<int>(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<int>.Success(0);
        }, cts.Token));
    }

    [Fact]
    public async Task Channel_UsedOutsideTransaction_Throws()
    {
        await ConnectAsync();
        IBusChannel? leaked = null;

        await _runtime.ExecuteAsync<int>((channel, _) =>
        {
            leaked = channel;
            return ValueTask.FromResult(Result<int>.Success(0));
        }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => leaked!.WriteAsync(Request, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task HeartbeatLost_TriggersReconnect()
    {
        await ConnectAsync();
        DrainEvents();

        Assert.True(_runtime.ReportHeartbeatLost().IsSuccess);

        await WaitUntilAsync(() => DrainEvents().Any(e => e.Transition.To == BusState.Connected), "重连成功");
        Assert.Equal(BusState.Connected, _runtime.State);
    }

    // ---------- 断开 ----------

    [Fact]
    public async Task Disconnect_ClosesTransport_AndIsIdempotent()
    {
        await ConnectAsync();

        Assert.True((await _runtime.DisconnectAsync(CancellationToken.None)).IsSuccess);
        Assert.Equal(BusState.Disconnected, _runtime.State);
        Assert.False(_sim.IsOpen);

        Assert.True((await _runtime.DisconnectAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Disconnect_InterruptsSlowOpen()
    {
        _sim.OpenDelay = TimeSpan.FromSeconds(1);
        Assert.True(_runtime.Connect().IsSuccess);
        await WaitForStateAsync(BusState.Connecting);

        var result = await _runtime.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(BusState.Disconnected, _runtime.State);
    }

    // ---------- 安全 ----------

    [Fact]
    public async Task SafetyTrip_AbortsInFlightTransaction_AndLatches()
    {
        await ConnectAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = _runtime.ExecuteAsync<int>(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<int>.Success(0);
        }, CancellationToken.None);

        await started.Task.WaitAsync(Guard);
        Assert.True(_runtime.SafetyTrip("急停按下").IsSuccess);

        var aborted = await inFlight.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(BusErrors.ChannelLockedCode, aborted.Error.Code);
        Assert.Equal(BusState.SafeStop, _runtime.State);
        await WaitUntilAsync(() => !_sim.IsOpen, "物理通道关闭");

        Assert.Equal(BusErrors.InvalidTransitionCode, _runtime.Connect().Error.Code);
        Assert.Equal(BusErrors.ChannelLockedCode, (await ExecuteEchoAsync()).Error.Code);
    }

    [Fact]
    public async Task ResetFromSafeStop_RequiresInterlockRelease()
    {
        await ConnectAsync();
        _runtime.SafetyTrip("门禁打开");
        _interlock.Active = true;

        var blocked = _runtime.Reset();

        Assert.Equal(BusErrors.InterlockActiveCode, blocked.Error.Code);
        Assert.Equal(BusState.SafeStop, _runtime.State);

        _interlock.Active = false;
        Assert.True(_runtime.Reset().IsSuccess);
        Assert.Equal(BusState.Disconnected, _runtime.State);
        await ConnectAsync();
    }

    [Fact]
    public async Task TransactionException_TripsSafety_AndIsRethrown()
    {
        await ConnectAsync();

        await Assert.ThrowsAsync<FormatException>(() => _runtime.ExecuteAsync<int>(
            (_, _) => throw new FormatException("协议解析错误"),
            CancellationToken.None));

        Assert.Equal(BusState.SafeStop, _runtime.State);
        var trip = Assert.Single(DrainEvents(), e => e.Transition.To == BusState.SafeStop);
        Assert.Equal(BusErrors.SafetyTrippedCode, trip.Cause?.Code);
    }

    // ---------- 释放 ----------

    [Fact]
    public async Task Dispose_ClosesTransport_CompletesEvents_AndRejectsUse()
    {
        await ConnectAsync();

        await _runtime.DisposeAsync();
        await _runtime.DisposeAsync();

        Assert.False(_sim.IsOpen);

        // Completion 只有在队列完成且剩余事件被读完后才结束：消费方读到末尾即可安全退出
        Assert.NotEmpty(DrainEvents());
        await _runtime.StateChanges.Completion.WaitAsync(Guard);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ExecuteEchoAsync());
        Assert.Throws<ObjectDisposedException>(() => _runtime.Connect());
        Assert.True(_runtime.SafetyTrip("释放后跳闸").IsSuccess);
    }

    [Fact]
    public async Task Dispose_WhileTransactionQueued_DoesNotTouchDisposedTransport()
    {
        await ConnectAsync();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _runtime.ExecuteAsync<int>(async (_, ct) =>
        {
            holding.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<int>.Success(0);
        }, CancellationToken.None);

        await holding.Task.WaitAsync(Guard);
        var queued = ExecuteEchoAsync();

        await _runtime.DisposeAsync();

        Assert.Equal(BusErrors.ChannelLockedCode, (await holder.WaitAsync(Guard)).Error.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Guard));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
