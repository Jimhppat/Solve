using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Application.Common;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.Buses.TestSupport;

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

    private readonly SwitchableTransportFactory _factory = new();
    private readonly FakeInterlock _interlock = new();
    private readonly BusRuntime _runtime;
    private readonly SimulatedTransport _sim;
    private readonly EventSubscription<BusStateChanged> _events;

    private static readonly BusRuntimeOptions Options = new()
    {
        ChannelLockTimeout = TimeSpan.FromMilliseconds(500),
        TransactionTimeout = TimeSpan.FromSeconds(1),
        ShutdownTimeout = TimeSpan.FromSeconds(2),
        FaultCooldown = TimeSpan.FromMilliseconds(50),
    };

    public BusRuntimeTests()
    {
        var descriptor = NewDescriptor();
        _runtime = BusRuntime.Create(descriptor, _factory, _interlock, NullLogger.Instance, Options).Value;
        Assert.True(_factory.TryGet(descriptor.Id, out _sim));
        _sim.Responder = request => request.ToArray();
        _events = _runtime.SubscribeStateChanges(out _);
    }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    private static TcpBusDescriptor NewDescriptor() => new(BusId.New(), "PLC-01", "127.0.0.1", 502)
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

    private List<BusStateChanged> DrainEvents() => Drain(_events);

    private static List<BusStateChanged> Drain(EventSubscription<BusStateChanged> subscription)
    {
        var events = new List<BusStateChanged>();
        while (subscription.Reader.TryRead(out var changed))
            events.Add(changed);
        return events;
    }

    // 按顺序收集目标状态序列，直到出现 until（含）
    private async Task<List<BusState>> CollectPathAsync(BusState until)
    {
        var path = new List<BusState>();
        using var guard = new CancellationTokenSource(Guard);
        while (path.Count == 0 || path[^1] != until)
            path.Add((await _events.Reader.ReadAsync(guard.Token)).Transition.To);

        return path;
    }

    private TcpBusDescriptor CurrentTcp => (TcpBusDescriptor)_runtime.Descriptor;

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

        // 按事件顺序断言心跳丢失之后的完整路径；"出现过任一 →Connected"会被早于心跳丢失的陈旧事件误满足
        Assert.Equal([BusState.Reconnecting, BusState.Connected], await CollectPathAsync(BusState.Connected));
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
        await _events.Reader.Completion.WaitAsync(Guard);

        // 释放后订阅：立即得到已完成的队列，消费方不会永久挂起
        using var late = _runtime.SubscribeStateChanges(out _);
        await late.Reader.Completion.WaitAsync(Guard);
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

    // ---------- 事件订阅（多订阅者） ----------

    [Fact]
    public async Task StateSubscribers_EachReceiveEveryTransition()
    {
        using var second = _runtime.SubscribeStateChanges(out var initial);
        Assert.Equal(BusState.Disconnected, initial);

        await ConnectAsync();

        // 旧实现是单读者通道：两个消费者会互相抢走事件
        var first = DrainEvents().Select(e => e.Transition.To).ToList();
        Assert.Equal([BusState.Connecting, BusState.Connected], first);
        Assert.Equal(first, Drain(second).Select(e => e.Transition.To));
    }

    [Fact]
    public async Task Subscribe_SnapshotsCurrentState_WithoutReplayingHistory()
    {
        await ConnectAsync();

        using var late = _runtime.SubscribeStateChanges(out var current);

        Assert.Equal(BusState.Connected, current);
        Assert.Empty(Drain(late));
    }

    [Fact]
    public async Task State_IsNeverObservedAheadOfItsEvent()
    {
        // 第 1 次取时间戳是测试线程上的 Disconnected → Connecting；第 2 次是监督任务上的 Connecting → Connected，
        // 在这里挂住，监督任务就停在"状态机已迁移、事件未入队"的临界区中间（仍持有运行时状态锁）
        using var clock = new BlockingTimeProvider(blockOnCall: 2, watchdog: Guard);
        await using var runtime = BusRuntime.Create(
            NewDescriptor(), new SimulatedTransportFactory(), _interlock, NullLogger.Instance, Options, clock).Value;
        using var events = runtime.SubscribeStateChanges(out _);

        Assert.True(runtime.Connect().IsSuccess);
        await clock.Blocked.WaitAsync(Guard);

        // 消费方常见写法：先看状态，再取事件（ConnectAsync + DrainEvents 就是这样）
        var observer = Task.Run(() => (State: runtime.State, Events: Drain(events)));
        try
        {
            // 旧实现读状态不经运行时状态锁，观察者会立刻带着 Connected 返回，而 →Connected 事件还不在队列里。
            // 等待只用于给旧实现暴露问题的机会；修复后观察者阻塞到放行为止，结果与这段等待时长无关。
            await Task.WhenAny(observer, Task.Delay(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            clock.Release();
        }

        var (state, seen) = await observer.WaitAsync(Guard);
        Assert.Equal(BusState.Connected, state);
        Assert.Equal([BusState.Connecting, BusState.Connected], seen.Select(e => e.Transition.To));
    }

    [Fact]
    public async Task DisposedSubscription_Completes_WithoutAffectingOthers()
    {
        var gone = _runtime.SubscribeStateChanges(out _);
        gone.Dispose();

        await ConnectAsync();

        await gone.Reader.Completion.WaitAsync(Guard);
        Assert.NotEmpty(DrainEvents());
    }

    // ---------- 重配置 ----------

    [Fact]
    public async Task Reconfigure_SoftParameters_KeepsTransportAndConnection()
    {
        await ConnectAsync();
        DrainEvents();

        var renamed = CurrentTcp with { Name = "PLC-01-B", Reconnect = CurrentTcp.Reconnect with { MaxAttempts = 9 } };
        Assert.True((await _runtime.ReconfigureAsync(renamed, CancellationToken.None)).IsSuccess);

        Assert.Equal(BusState.Connected, _runtime.State);
        Assert.Equal("PLC-01-B", _runtime.Descriptor.Name);
        Assert.True(_sim.IsOpen);
        Assert.Empty(DrainEvents());
        Assert.True((await ExecuteEchoAsync()).IsSuccess);
    }

    [Fact]
    public async Task Reconfigure_HardParameters_WhileConnected_SwapsTransportAndReconnects()
    {
        await ConnectAsync();
        DrainEvents();

        var moved = CurrentTcp with { Port = 503 };
        Assert.True((await _runtime.ReconfigureAsync(moved, CancellationToken.None)).IsSuccess);

        Assert.Equal(
            [BusState.Disconnecting, BusState.Disconnected, BusState.Connecting, BusState.Connected],
            await CollectPathAsync(BusState.Connected));
        Assert.Equal(503, CurrentTcp.Port);

        // 旧通道已释放；当前通道是工厂为新配置创建的实例
        Assert.False(_sim.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _sim.OpenAsync(CancellationToken.None).AsTask());
        Assert.True(_factory.TryGet(moved.Id, out var replacement));
        Assert.NotSame(_sim, replacement);
        Assert.True(replacement.IsOpen);

        replacement.Responder = request => request.ToArray();
        Assert.True((await ExecuteEchoAsync()).IsSuccess);
    }

    [Fact]
    public async Task Reconfigure_HardParameters_WhileDisconnected_SwapsWithoutConnecting()
    {
        var moved = CurrentTcp with { Host = "127.0.0.2" };
        Assert.True((await _runtime.ReconfigureAsync(moved, CancellationToken.None)).IsSuccess);

        Assert.Equal(BusState.Disconnected, _runtime.State);
        Assert.Empty(DrainEvents());
        Assert.True(_factory.TryGet(moved.Id, out var replacement));
        Assert.NotSame(_sim, replacement);
        Assert.False(replacement.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _sim.OpenAsync(CancellationToken.None).AsTask());

        await ConnectAsync();
        Assert.True(replacement.IsOpen);
    }

    [Fact]
    public async Task Reconfigure_FactoryFailure_LeavesBusUntouched()
    {
        await ConnectAsync();
        var original = _runtime.Descriptor;
        _factory.FailWith = BusErrors.UnsupportedDescriptor(typeof(TcpBusDescriptor));

        var result = await _runtime.ReconfigureAsync(CurrentTcp with { Port = 503 }, CancellationToken.None);

        // 旧实现先释放旧通道再创建新通道：创建失败后总线只剩一个已释放的通道，重启前无法再连接
        Assert.Equal(BusErrors.UnsupportedDescriptorCode, result.Error.Code);
        Assert.Same(original, _runtime.Descriptor);
        Assert.Equal(BusState.Connected, _runtime.State);
        Assert.True(_sim.IsOpen);
        Assert.True((await ExecuteEchoAsync()).IsSuccess);
    }

    [Fact]
    public async Task Reconfigure_DuringSlowOpen_InterruptsOpen_InsteadOfFailingOnChannelLock()
    {
        // 连接超时 2 s 远大于通道锁等待 500 ms：旧实现在拿锁后才取消会话，会因等锁超时返回 ChannelBusy
        var slow = CurrentTcp with { Timeouts = CurrentTcp.Timeouts with { Connect = TimeSpan.FromSeconds(2) } };
        Assert.True((await _runtime.ReconfigureAsync(slow, CancellationToken.None)).IsSuccess);

        _sim.OpenDelay = TimeSpan.FromSeconds(2);
        Assert.True(_runtime.Connect().IsSuccess);
        await Task.Delay(100); // 让监督任务进入打开并持有通道锁
        Assert.Equal(BusState.Connecting, _runtime.State);

        var result = await _runtime.ReconfigureAsync(slow with { Port = 503 }, CancellationToken.None).WaitAsync(Guard);

        Assert.True(result.IsSuccess, result.ToString());
        await WaitForStateAsync(BusState.Connected);
        Assert.Equal(503, CurrentTcp.Port);
    }

    [Fact]
    public async Task Reconfigure_ConcurrentRequests_AreAppliedInOrder_WithMatchingTransport()
    {
        await ConnectAsync();

        var first = _runtime.ReconfigureAsync(CurrentTcp with { Port = 503 }, CancellationToken.None);
        var second = _runtime.ReconfigureAsync(CurrentTcp with { Port = 504 }, CancellationToken.None);

        var results = await Task.WhenAll(first, second).WaitAsync(Guard);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        await WaitForStateAsync(BusState.Connected);

        // 描述与通道成对更新：最终配置是后到达的请求，且正在使用的就是为它创建的通道
        Assert.Equal(504, CurrentTcp.Port);
        Assert.True(_factory.TryGet(_runtime.Id, out var latest));
        Assert.True(latest.IsOpen);
    }

    [Fact]
    public async Task Reconfigure_MismatchedId_IsRejected()
    {
        var other = CurrentTcp with { Id = BusId.New() };

        var result = await _runtime.ReconfigureAsync(other, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(BusErrors.DescriptorCodePrefix + "Id", result.Error.Code);
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
