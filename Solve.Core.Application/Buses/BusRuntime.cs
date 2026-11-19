using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Application.Common;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses;

/// <summary>
/// 单条总线的运行时内核：状态机 + 物理通道 + 通道锁 + 监督（连接/重连/关闭）循环。
/// </summary>
/// <remarks>
/// <para>线程模型：</para>
/// <list type="bullet">
/// <item>监督任务（每条总线 1 个）是物理通道生命周期的唯一执行者：打开、关闭、重连、硬参数重配置时的通道替换
/// 都只在这里发生。其他线程只能投递请求并唤醒它（控制面的“单所有者”）。</item>
/// <item>协议事务在调用方线程执行，通过 <see cref="ExecuteAsync{T}"/> 由通道锁串行化；
/// 监督任务的 Open/Close 也持有同一把锁，因此物理通道上任意时刻只有一个操作。</item>
/// <item>所有状态迁移经 <see cref="Fire"/> 在 _stateGate 内完成，事件按迁移顺序入队；
/// 对外的 <see cref="State"/> 也经 _stateGate 读取，读到的状态不会领先于已发布的事件。</item>
/// </list>
/// <para>故障安全：</para>
/// <list type="bullet">
/// <item>离开 Connected 时立即取消 I/O 令牌，在途事务马上中止并返回 Bus.ChannelLocked。</item>
/// <item>断开、安全跳闸、释放会取消会话令牌，打断监督任务正在进行的打开或退避等待。</item>
/// <item>事务回调或监督循环中的未处理异常一律触发 SafetyTrip 锁存，需联锁许可后人工复位。</item>
/// </list>
/// </remarks>
public sealed class BusRuntime : IAsyncDisposable
{
    private const int MaxLoggedPacketBytes = 256;

    private BusDescriptor _descriptor;
    private IBusTransport _transport;
    private readonly IBusTransportFactory _transportFactory;
    private readonly IBusSafetyInterlock _interlock;
    private readonly ILogger _logger;
    private readonly BusRuntimeOptions _options;
    private readonly TimeProvider _time;

    private readonly BusStateMachine _machine = new();
    private readonly Lock _stateGate = new();

    // 未使用 AvailableWaitHandle，不持有非托管资源，故不 Dispose：释放后仍在等待的事务不会因此收到 ObjectDisposedException
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    // 容量 1 + DropWrite：多次唤醒合并为一次，且不会丢失"有新变化"这一事实
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly EventBroadcaster<BusStateChanged> _events;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TransactionChannel _channel;

    // 串行化所有重配置请求（软/硬参数），保证描述与物理通道始终成对更新
    private readonly SemaphoreSlim _reconfigureGate = new(1, 1);

    // 待监督任务执行的硬参数重建请求，受 _stateGate 保护；同一时刻至多一个（由 _reconfigureGate 保证）
    private PendingRebuild? _pendingRebuild;

    // 会话令牌：Connect 时新建，Disconnect / SafetyTrip / Dispose 时取消。打断监督任务的打开与退避等待。
    // 纯 CTS（无定时器、非链接）不持有资源，被替换后无需 Dispose，避免他人持有的 Token 访问已释放对象。
    private CancellationTokenSource _session = CreateCanceledSource();

    // I/O 令牌：进入 Connected 时新建，离开 Connected 时取消。中止在途事务。
    private CancellationTokenSource _io = CreateCanceledSource();

    private TaskCompletionSource _statePulse = NewPulse();
    private Task _supervisor = Task.CompletedTask;
    private int _disposed;

    private BusRuntime(
        BusDescriptor descriptor,
        IBusTransport transport,
        IBusTransportFactory transportFactory,
        IBusSafetyInterlock interlock,
        ILogger logger,
        BusRuntimeOptions options,
        TimeProvider time)
    {
        _descriptor = descriptor;
        _transport = transport;
        _transportFactory = transportFactory;
        _interlock = interlock;
        _logger = logger;
        _options = options;
        _time = time;
        _channel = new TransactionChannel(this);
        _events = new EventBroadcaster<BusStateChanged>(options.StateEventCapacity);
    }

    /// <summary>
    /// 校验配置、创建（未打开的）物理通道并启动监督任务。初始状态为 Disconnected，不会自动连接。
    /// </summary>
    public static Result<BusRuntime> Create(
        BusDescriptor descriptor,
        IBusTransportFactory transportFactory,
        IBusSafetyInterlock interlock,
        ILogger logger,
        BusRuntimeOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(interlock);
        ArgumentNullException.ThrowIfNull(logger);

        options ??= BusRuntimeOptions.Default;
        options.EnsureValid();

        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result<BusRuntime>.Failure(BusErrors.InvalidDescriptor(errors));

        var transport = transportFactory.Create(descriptor);
        if (transport.IsFailure)
            return Result<BusRuntime>.Failure(transport.Error);

        var runtime = new BusRuntime(
            descriptor, transport.Value, transportFactory, interlock, logger, options, timeProvider ?? TimeProvider.System);
        runtime._supervisor = Task.Run(runtime.SuperviseAsync);
        return Result<BusRuntime>.Success(runtime);
    }

    public BusId Id => _descriptor.Id;

    public BusDescriptor Descriptor
    {
        get
        {
            lock (_stateGate)
            {
                return _descriptor;
            }
        }
    }

    /// <summary>
    /// 当前状态，与状态事件线性一致：读到某个状态时，导致它的迁移事件已进入所有订阅队列。
    /// </summary>
    public BusState State
    {
        get
        {
            // 必须经运行时状态锁读取：Fire 在同一临界区内先迁移状态机、再取时间戳并发布事件。
            // 只经状态机自身的锁读取，会在这两步之间读到新状态，而对应事件尚未入队；
            // "先看状态、再取事件"的消费方会因此漏掉该事件，随后又把它当作新事件收到。
            lock (_stateGate)
            {
                return _machine.State;
            }
        }
    }

    /// <summary>
    /// 订阅状态变化事件，并原子地取得订阅时刻的状态：<paramref name="current"/> 之后的每一次迁移都会进入返回的队列，
    /// 不重复、不遗漏。每个订阅者独立排队（UI、协议层、告警可同时订阅），用完 Dispose 退订。
    /// </summary>
    /// <remarks>消费方负责切换到 UI 线程。运行时释放后队列完成；释放后订阅得到已完成的队列。</remarks>
    public EventSubscription<BusStateChanged> SubscribeStateChanges(out BusState current)
    {
        // 与 Fire 共用状态锁：快照与登记之间不会插入迁移
        lock (_stateGate)
        {
            current = _machine.State;
            return _events.Subscribe();
        }
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 请求连接。立即返回，连接结果通过 <see cref="StateChanges"/> 观察。
    /// 仅 Disconnected 可连接；Faulted / SafeStop 必须先 <see cref="Reset"/>。
    /// </summary>
    public Result Connect()
    {
        ThrowIfDisposed();
        return ToResult(Fire(BusTrigger.Connect, cause: null, onAccepted: () => _session = new CancellationTokenSource()));
    }

    /// <summary>
    /// 断开并等待物理通道释放完成。已断开时直接成功（幂等）。
    /// </summary>
    public async Task<Result> DisconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (State == BusState.Disconnected)
            return Result.Success();

        var fired = Fire(BusTrigger.Disconnect, cause: null);
        if (fired.IsFailure)
            return Result.Failure(fired.Error);

        CancelSession();

        var settled = await WaitForStateAsync(s => s != BusState.Disconnecting, _options.ShutdownTimeout, cancellationToken)
            .ConfigureAwait(false);

        return settled switch
        {
            null => Result.Failure(BusErrors.Timeout("断开", _options.ShutdownTimeout)),
            BusState.Disconnected => Result.Success(),
            // 断开过程中发生安全跳闸：通道同样已被关闭，但状态是锁存态，需要如实告知调用方
            { } other => Result.Failure(BusErrors.ChannelLocked(other)),
        };
    }

    /// <summary>
    /// 安全跳闸：任何状态下立即锁存 SafeStop、锁定通道、中止在途事务并关闭物理通道。
    /// 可从任意线程调用，释放后调用为空操作。
    /// </summary>
    public Result SafetyTrip(string reason)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return Result.Success();

        var fired = Fire(BusTrigger.SafetyTrip, BusErrors.SafetyTripped(reason));
        CancelSession();
        return ToResult(fired);
    }

    /// <summary>
    /// 人工复位 Faulted / SafeStop → Disconnected。SafeStop 复位前必须通过安全联锁检查。
    /// </summary>
    public Result Reset()
    {
        ThrowIfDisposed();
        return ToResult(Fire(BusTrigger.Reset, cause: null, guard: CheckResetInterlock));
    }

    /// <summary>
    /// 上层（协议心跳）报告心跳丢失，Connected → Reconnecting。
    /// </summary>
    public Result ReportHeartbeatLost()
    {
        ThrowIfDisposed();
        return ToResult(Fire(BusTrigger.HeartbeatLost, BusErrors.HeartbeatLost()));
    }

    /// <summary>
    /// 重配置总线参数。软参数（Tier 1：名称、超时、重连策略）在线热更新；
    /// 硬参数（Tier 2：链路类型、端口、波特率、主机等）由监督任务在安全点替换物理通道。
    /// </summary>
    /// <remarks>
    /// <para>并发：多次调用按到达顺序串行执行，描述与物理通道始终成对更新。</para>
    /// <para>硬参数流程：先创建新通道（无 I/O），失败则直接返回、旧通道与状态原封不动；
    /// 成功后把请求交给监督任务，由它中止在途事务、关闭旧通道、换上新通道，原本处于活动状态的总线随后自动重连。</para>
    /// <para><paramref name="cancellationToken"/> 只作用于排队等待；请求一旦交给监督任务就会执行完，
    /// 不会中途放弃，避免调用方误判结果（例如据此回滚数据库）。</para>
    /// </remarks>
    public async Task<Result> ReconfigureAsync(BusDescriptor newDescriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newDescriptor);
        ThrowIfDisposed();

        if (newDescriptor.Id != Id)
            return Result.Failure(BusErrors.InvalidField("Id", $"描述符 Id {newDescriptor.Id} 与总线 Id {Id} 不匹配。"));

        var errors = newDescriptor.Validate();
        if (errors.Count > 0)
            return Result.Failure(BusErrors.InvalidDescriptor(errors));

        await _reconfigureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var oldDescriptor = Descriptor;
            if (!oldDescriptor.RequiresTransportRebuild(newDescriptor))
            {
                lock (_stateGate)
                {
                    _descriptor = newDescriptor;
                }

                _logger.LogInformation("Bus {BusId} 软参数热更新成功", Id);
                return Result.Success();
            }

            // 先建后拆：创建不做 I/O，失败时旧通道仍完好可用
            var created = _transportFactory.Create(newDescriptor);
            if (created.IsFailure)
            {
                _logger.LogWarning("Bus {BusId} 重配置创建新物理通道失败，保持原配置：{Error}", Id, created.Error);
                return Result.Failure(created.Error);
            }

            var request = new PendingRebuild(
                newDescriptor,
                created.Value,
                new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously));

            CancellationTokenSource session;
            bool accepted;
            lock (_stateGate)
            {
                // 与 DisposeAsync 收尾互斥：释放已开始时不再投递，否则请求无人处理
                accepted = Volatile.Read(ref _disposed) == 0;
                if (accepted)
                    _pendingRebuild = request;

                session = _session;
            }

            if (!accepted)
            {
                await created.Value.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(GetType().FullName);
            }

            // 打断监督任务正在进行的打开 / 退避等待并唤醒它，使其尽快在循环顶部执行替换
            session.Cancel();
            _wake.Writer.TryWrite(true);

            return await request.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _reconfigureGate.Release();
        }
    }

    // ==================== 事务 ====================

    /// <summary>
    /// 在通道锁内执行一个协议事务（如"清缓冲 → 发请求 → 收完整响应"），保证事务之间不交错。
    /// </summary>
    /// <remarks>
    /// 事务总时长受 <see cref="BusRuntimeOptions.TransactionTimeout"/> 约束。
    /// 回调抛出的非取消异常视为未知通信状态：触发 SafetyTrip 后原样抛出。
    /// </remarks>
    public async Task<Result<T>> ExecuteAsync<T>(
        Func<IBusChannel, CancellationToken, ValueTask<Result<T>>> transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ThrowIfDisposed();

        // 快速失败：未连接时不排队等锁
        var state = State;
        if (!BusStateMachine.AllowsIo(state))
            return Result<T>.Failure(BusErrors.ChannelLocked(state));

        if (!await _channelLock.WaitAsync(_options.ChannelLockTimeout, cancellationToken).ConfigureAwait(false))
            return Result<T>.Failure(BusErrors.ChannelBusy(_options.ChannelLockTimeout));

        try
        {
            // 排队期间运行时可能已被释放：不得再触碰已释放的物理通道
            ThrowIfDisposed();

            CancellationToken io;
            lock (_stateGate)
            {
                state = _machine.State;
                io = _io.Token;
            }

            if (!BusStateMachine.AllowsIo(state))
                return Result<T>.Failure(BusErrors.ChannelLocked(state));

            using var tx = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, io);
            tx.CancelAfter(_options.TransactionTimeout);

            _channel.Begin(cancellationToken, io, tx.Token);
            try
            {
                return await transaction(_channel, tx.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && tx.IsCancellationRequested)
            {
                return Result<T>.Failure(io.IsCancellationRequested
                    ? BusErrors.ChannelLocked(State)
                    : BusErrors.Timeout("事务", _options.TransactionTimeout));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogCritical(ex, "Bus {BusId} 事务抛出未处理异常，执行安全跳闸", Id);
                SafetyTrip($"事务未处理异常 {ex.GetType().Name}");
                throw;
            }
            finally
            {
                _channel.End();
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    // ==================== 释放 ====================

    /// <summary>
    /// 停止监督任务、中止在途事务、关闭并释放物理通道。幂等。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        CancellationTokenSource session, io;
        lock (_stateGate)
        {
            session = _session;
            io = _io;
        }

        _lifetime.Cancel();
        session.Cancel();
        io.Cancel();

        try
        {
            await _supervisor.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogError("Bus {BusId} 监督任务未在 {Timeout} 内退出，强制关闭物理通道", Id, _options.ShutdownTimeout);
        }

        // 监督任务已退出：尚未执行的重建请求不会再被处理，释放其新通道并通知等待方
        PendingRebuild? orphan;
        lock (_stateGate)
        {
            orphan = _pendingRebuild;
            _pendingRebuild = null;
        }

        if (orphan is not null)
        {
            await orphan.Transport.DisposeAsync().ConfigureAwait(false);
            orphan.Completion.TrySetException(new ObjectDisposedException(GetType().FullName));
        }

        try
        {
            await CloseTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _events.Complete();
            _lifetime.Dispose();
            _logger.LogInformation("Bus {BusId} 运行时已释放", Id);
        }
    }

    // ==================== 监督循环 ====================

    private async Task SuperviseAsync()
    {
        var lifetime = _lifetime.Token;
        var attempts = 0;

        while (!lifetime.IsCancellationRequested)
        {
            BusState state;
            CancellationToken session;
            PendingRebuild? rebuild;
            lock (_stateGate)
            {
                state = _machine.State;
                session = _session.Token;
                rebuild = _pendingRebuild;
                _pendingRebuild = null;
            }

            if (state != BusState.Reconnecting)
                attempts = 0;

            try
            {
                // 重配置请求优先于常规状态处理：在两步操作之间的安全点替换物理通道
                if (rebuild is not null)
                {
                    await ApplyRebuildAsync(rebuild).ConfigureAwait(false);
                    continue;
                }

                switch (state)
                {
                    case BusState.Connecting:
                        await OpenOnceAsync(session).ConfigureAwait(false);
                        break;

                    case BusState.Reconnecting:
                        await CloseTransportAsync().ConfigureAwait(false);

                        if (_descriptor.Reconnect.IsExhausted(attempts))
                        {
                            Fire(BusTrigger.RetryExhausted, BusErrors.RetryExhausted(attempts));
                            break;
                        }

                        attempts++;
                        await Task.Delay(_descriptor.Reconnect.GetDelay(attempts), _time, session).ConfigureAwait(false);
                        await OpenOnceAsync(session).ConfigureAwait(false);
                        break;

                    case BusState.Disconnecting:
                        await CloseTransportAsync().ConfigureAwait(false);
                        Fire(BusTrigger.Closed, cause: null);
                        break;

                    case BusState.Faulted:
                    case BusState.SafeStop:
                        // 锁存态：保证物理通道已释放，然后等待复位
                        await CloseTransportAsync().ConfigureAwait(false);
                        await WaitForWakeAsync(lifetime).ConfigureAwait(false);
                        break;

                    default:
                        await WaitForWakeAsync(lifetime).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // 断开 / 安全跳闸打断了当前步骤：回到循环顶部按新状态处理
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Bus {BusId} 监督循环未处理异常，执行安全跳闸", Id);
                Fire(BusTrigger.SafetyTrip, BusErrors.UnhandledException(ex));
                CancelSession();

                try
                {
                    await Task.Delay(_options.FaultCooldown, _time, lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task OpenOnceAsync(CancellationToken session)
    {
        // Reset 后可能残留未关闭的句柄，先关闭保证 Open 从干净状态开始
        await CloseTransportAsync().ConfigureAwait(false);

        var connectTimeout = _descriptor.Timeouts.Connect;
        Result result;

        if (!await _channelLock.WaitAsync(_options.ChannelLockTimeout, session).ConfigureAwait(false))
        {
            result = Result.Failure(BusErrors.ChannelBusy(_options.ChannelLockTimeout));
        }
        else
        {
            try
            {
                using var op = CancellationTokenSource.CreateLinkedTokenSource(session);
                op.CancelAfter(connectTimeout);
                try
                {
                    result = await _transport.OpenAsync(op.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!session.IsCancellationRequested)
                {
                    result = Result.Failure(BusErrors.Timeout("打开物理通道", connectTimeout));
                }
            }
            finally
            {
                _channelLock.Release();
            }
        }

        if (result.IsFailure)
        {
            _logger.LogWarning("Bus {BusId} 打开物理通道失败：{Error}", Id, result.Error);
            Fire(BusTrigger.OpenFailed, result.Error);
            return;
        }

        if (Fire(BusTrigger.OpenSucceeded, cause: null).IsFailure)
        {
            // 打开期间状态被并发改变（理论上会先取消会话令牌），保险起见释放刚打开的句柄
            await CloseTransportAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在监督任务内执行硬参数重建：离开活动状态（中止在途事务）→ 通道锁内关旧换新 → 锁外释放旧通道 → 原先活动则自动重连。
    /// </summary>
    private async Task ApplyRebuildAsync(PendingRebuild rebuild)
    {
        var swapped = false;
        try
        {
            // 只有活动状态接受 Disconnect；离开 Connected 时 Fire 会取消 I/O 令牌，在途事务立即以 ChannelLocked 结束
            var resume = State is BusState.Connecting or BusState.Connected or BusState.Reconnecting
                && Fire(BusTrigger.Disconnect, cause: null).IsSuccess;

            var old = await SwapTransportAsync(rebuild.Transport, rebuild.Descriptor).ConfigureAwait(false);
            swapped = true;

            try
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bus {BusId} 释放旧物理通道异常", Id);
            }

            // 替换期间若发生安全跳闸，状态已锁存为 SafeStop，下面的迁移会被拒绝，总线保持锁存
            if (resume && Fire(BusTrigger.Closed, cause: null).IsSuccess)
                Fire(BusTrigger.Connect, cause: null, onAccepted: () => _session = new CancellationTokenSource());

            _logger.LogInformation("Bus {BusId} 物理通道已替换为 {Type}，{Next}",
                Id, rebuild.Descriptor.GetType().Name, resume ? "自动重连" : "保持原状态");
            rebuild.Completion.TrySetResult(Result.Success());
        }
        catch (Exception ex)
        {
            if (!swapped)
                await rebuild.Transport.DisposeAsync().ConfigureAwait(false);

            rebuild.Completion.TrySetResult(Result.Failure(BusErrors.UnhandledException(ex)));
            throw; // 交给监督循环兜底：安全跳闸
        }
    }

    /// <summary>
    /// 在通道锁内关闭旧通道并换上新通道（与事务、打开、关闭互斥），返回旧通道由调用方在锁外释放。
    /// 锁等待超时同样强制替换：释放硬件优先于等待在途事务（与 <see cref="CloseTransportAsync"/> 一致）。
    /// </summary>
    private async Task<IBusTransport> SwapTransportAsync(IBusTransport replacement, BusDescriptor descriptor)
    {
        var acquired = await _channelLock.WaitAsync(_options.ChannelLockTimeout).ConfigureAwait(false);
        if (!acquired)
            _logger.LogWarning("Bus {BusId} 等待通道锁超过 {Timeout}，强制替换物理通道", Id, _options.ChannelLockTimeout);

        try
        {
            var old = _transport;
            try
            {
                using var timeout = new CancellationTokenSource(_descriptor.Timeouts.Connect);
                await old.CloseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 旧通道马上会被释放，关闭失败不阻止替换
                _logger.LogWarning(ex, "Bus {BusId} 关闭旧物理通道异常，继续替换", Id);
            }

            lock (_stateGate)
            {
                _transport = replacement;
                _descriptor = descriptor;
            }

            return old;
        }
        finally
        {
            if (acquired)
                _channelLock.Release();
        }
    }

    /// <summary>
    /// 在通道锁内关闭物理通道。锁等待超时也会强制关闭：释放硬件优先于等待在途事务。
    /// 不受会话/生命周期令牌影响，确保断开、跳闸、释放时一定执行。
    /// </summary>
    private async Task CloseTransportAsync()
    {
        var acquired = await _channelLock.WaitAsync(_options.ChannelLockTimeout).ConfigureAwait(false);
        if (!acquired)
            _logger.LogWarning("Bus {BusId} 等待通道锁超过 {Timeout}，强制关闭物理通道", Id, _options.ChannelLockTimeout);

        try
        {
            using var timeout = new CancellationTokenSource(_descriptor.Timeouts.Connect);
            await _transport.CloseAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                _channelLock.Release();
        }
    }

    private async Task WaitForWakeAsync(CancellationToken lifetime)
    {
        if (await _wake.Reader.WaitToReadAsync(lifetime).ConfigureAwait(false))
            _wake.Reader.TryRead(out _);
    }

    // ==================== 状态迁移 ====================

    /// <summary>
    /// 唯一的状态迁移入口。guard 与 onAccepted 在状态锁内执行，必须快速且无 I/O。
    /// </summary>
    private Result<BusTransition> Fire(
        BusTrigger trigger,
        Error? cause,
        Func<Error?>? guard = null,
        Action? onAccepted = null)
    {
        Result<BusTransition> result;
        BusStateChanged? changed = null;
        TaskCompletionSource? pulse = null;
        CancellationTokenSource? ioToCancel = null;

        lock (_stateGate)
        {
            if (guard?.Invoke() is { } rejected)
            {
                result = Result<BusTransition>.Failure(rejected);
            }
            else
            {
                result = _machine.Fire(trigger);
                if (result.IsSuccess)
                {
                    var transition = result.Value;

                    if (transition.From == BusState.Connected && transition.To != BusState.Connected)
                        ioToCancel = _io;
                    else if (transition.From != BusState.Connected && transition.To == BusState.Connected)
                        _io = new CancellationTokenSource();

                    onAccepted?.Invoke();

                    changed = new BusStateChanged(Id, transition, _time.GetUtcNow(), cause);
                    _events.Publish(changed);

                    pulse = _statePulse;
                    _statePulse = NewPulse();
                }
            }
        }

        if (changed is null)
        {
            _logger.LogDebug("Bus {BusId} 拒绝触发 {Trigger}：{Error}", Id, trigger, result.Error);
            return result;
        }

        // 取消回调与等待者的续体都在锁外执行，避免在状态锁内运行外部代码
        ioToCancel?.Cancel();
        pulse!.TrySetResult();
        _wake.Writer.TryWrite(true);
        LogTransition(changed);
        return result;
    }

    private Error? CheckResetInterlock()
    {
        if (_machine.State != BusState.SafeStop)
            return null;

        var check = _interlock.CanReset(Id);
        return check.IsFailure ? check.Error : null;
    }

    private async Task<BusState?> WaitForStateAsync(Func<BusState, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);

        while (true)
        {
            BusState state;
            Task pulse;
            lock (_stateGate)
            {
                state = _machine.State;
                pulse = _statePulse.Task;
            }

            if (predicate(state))
                return state;

            try
            {
                await pulse.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private void CancelSession()
    {
        CancellationTokenSource session;
        lock (_stateGate)
        {
            session = _session;
        }

        session.Cancel();
    }

    // ==================== 链路故障与日志 ====================

    private void OnLinkFailure(string operation, Error error, ReadOnlyMemory<byte> packet)
    {
        if (packet.IsEmpty)
        {
            _logger.LogWarning("Bus {BusId} {Operation} 链路故障：{Error}", Id, operation, error);
        }
        else
        {
            var shown = packet.Length > MaxLoggedPacketBytes ? packet[..MaxLoggedPacketBytes] : packet;
            _logger.LogWarning("Bus {BusId} {Operation} 链路故障：{Error}，报文({Length} B)={Packet}",
                Id, operation, error, packet.Length, Convert.ToHexString(shown.Span));
        }

        // 已不在 Connected（其他事务或心跳先报告了故障）时迁移会被拒绝，属正常情况
        Fire(BusTrigger.IoFailed, error);
    }

    private void LogTransition(BusStateChanged changed)
    {
        var level = changed.Transition.To switch
        {
            BusState.SafeStop or BusState.Faulted => LogLevel.Error,
            BusState.Reconnecting => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        _logger.Log(level, "Bus {BusId} 状态 {From} -[{Trigger}]-> {To} 原因={Cause}",
            Id, changed.Transition.From, changed.Transition.Trigger, changed.Transition.To,
            changed.Cause?.ToString() ?? "-");
    }

    // ==================== 工具 ====================

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private static Result ToResult(Result<BusTransition> result)
        => result.IsSuccess ? Result.Success() : Result.Failure(result.Error);

    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationTokenSource CreateCanceledSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }

    /// <summary>
    /// 一次待执行的硬参数重建：新描述 + 已创建（未打开）的新通道 + 完成通知
    /// </summary>
    private sealed record PendingRebuild(BusDescriptor Descriptor, IBusTransport Transport, TaskCompletionSource<Result> Completion);

    // ==================== 事务通道 ====================

    /// <summary>
    /// 事务串行执行，因此整个运行时只复用一个实例，避免每个事务分配对象。
    /// Begin/End 之外的调用视为把通道泄露出事务，直接抛异常。
    /// </summary>
    private sealed class TransactionChannel(BusRuntime owner) : IBusChannel
    {
        private CancellationToken _caller;
        private CancellationToken _io;
        private CancellationToken _tx;
        private int _active;

        public BusId BusId => owner.Id;

        public void Begin(CancellationToken caller, CancellationToken io, CancellationToken tx)
        {
            _caller = caller;
            _io = io;
            _tx = tx;
            Volatile.Write(ref _active, 1);
        }

        public void End() => Volatile.Write(ref _active, 0);

        public async ValueTask<Result<int>> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (CheckUsable() is { } blocked)
                return Result<int>.Failure(blocked);

            var timeout = owner._descriptor.Timeouts.Read;
            using var op = CreateOperationSource(cancellationToken, timeout);
            try
            {
                var result = await owner._transport.ReadAsync(buffer, op.Token).ConfigureAwait(false);
                if (result.IsFailure)
                    owner.OnLinkFailure("Read", result.Error, ReadOnlyMemory<byte>.Empty);

                return result;
            }
            catch (OperationCanceledException)
            {
                var error = ClassifyCancellation(cancellationToken, "读", timeout);
                if (error is null)
                    throw;

                return Result<int>.Failure(error);
            }
        }

        public async ValueTask<Result> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (CheckUsable() is { } blocked)
                return Result.Failure(blocked);

            var timeout = owner._descriptor.Timeouts.Write;
            using var op = CreateOperationSource(cancellationToken, timeout);
            try
            {
                var result = await owner._transport.WriteAsync(data, op.Token).ConfigureAwait(false);
                if (result.IsFailure)
                    owner.OnLinkFailure("Write", result.Error, data);

                return result;
            }
            catch (OperationCanceledException)
            {
                var error = ClassifyCancellation(cancellationToken, "写", timeout);
                if (error is null)
                    throw;

                return Result.Failure(error);
            }
        }

        public async ValueTask<Result> DiscardInputAsync(CancellationToken cancellationToken)
        {
            if (CheckUsable() is { } blocked)
                return Result.Failure(blocked);

            var timeout = owner._descriptor.Timeouts.Read;
            using var op = CreateOperationSource(cancellationToken, timeout);
            try
            {
                var result = await owner._transport.DiscardInputAsync(op.Token).ConfigureAwait(false);
                if (result.IsFailure)
                    owner.OnLinkFailure("DiscardInput", result.Error, ReadOnlyMemory<byte>.Empty);

                return result;
            }
            catch (OperationCanceledException)
            {
                var error = ClassifyCancellation(cancellationToken, "清空接收缓冲", timeout);
                if (error is null)
                    throw;

                return Result.Failure(error);
            }
        }

        private Error? CheckUsable()
        {
            if (Volatile.Read(ref _active) == 0)
                throw new InvalidOperationException("IBusChannel 只能在 ExecuteAsync 的事务回调内使用。");

            // 事务中途总线已离开 Connected（例如心跳丢失），后续操作不得再触碰物理通道
            var state = owner.State;
            return BusStateMachine.AllowsIo(state) ? null : BusErrors.ChannelLocked(state);
        }

        private CancellationTokenSource CreateOperationSource(CancellationToken cancellationToken, TimeSpan timeout)
        {
            var op = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tx);
            op.CancelAfter(timeout);
            return op;
        }

        /// <summary>
        /// 区分取消来源。返回 null 表示是调用方自己的取消，应原样抛出。
        /// </summary>
        private Error? ClassifyCancellation(CancellationToken cancellationToken, string operation, TimeSpan timeout)
        {
            if (_caller.IsCancellationRequested)
                return null;

            if (_io.IsCancellationRequested)
                return BusErrors.ChannelLocked(owner.State);

            if (_tx.IsCancellationRequested)
                return BusErrors.Timeout("事务", owner._options.TransactionTimeout);

            if (cancellationToken.IsCancellationRequested)
                return null;

            // 单次操作超时只代表设备未及时应答（站号错误、设备忙），不判定为链路故障，由协议层决定是否升级
            owner._logger.LogWarning("Bus {BusId} {Operation}超时（{Timeout}）", owner.Id, operation, timeout);
            return BusErrors.Timeout(operation, timeout);
        }
    }
}
