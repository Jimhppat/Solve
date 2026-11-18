using System.Threading.Channels;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Hardware.Buses.Simulation;

/// <summary>
/// 仿真物理通道：不触碰任何真实硬件，用于 Dry-Run、上位机联调和总线/协议层的故障注入测试。
/// </summary>
/// <remarks>
/// 数据流：WriteAsync → <see cref="Responder"/>（模拟设备，可选，可延迟）→ 有界接收队列 → ReadAsync。
/// 测试代码也可通过 <see cref="InjectRx"/> 直接注入字节（模拟主动上报、半包、粘包）。
/// <para>
/// 线程模型：I/O 方法（Open/Read/Write/Discard）遵循 IBusTransport 约定，由调用方串行化；
/// 故障注入与配置成员（Fail*、InjectRx、DropConnection、Responder、*Delay）可在任意线程调用。
/// </para>
/// </remarks>
public sealed class SimulatedTransport : IBusTransport
{
    /// <summary>
    /// 接收队列容量（按块计）。满时新数据被丢弃并计入 <see cref="DroppedChunks"/>，模拟接收缓冲区溢出。
    /// </summary>
    public const int RxQueueCapacity = 256;

    /// <summary>
    /// 发送历史保留条数，超出后丢弃最旧记录，避免长时间仿真运行时内存无界增长
    /// </summary>
    public const int TxHistoryCapacity = 256;

    private enum LinkState
    {
        Closed,
        Open,
        Disposed,
    }

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<byte[]> _txHistory = new();

    private LinkState _state = LinkState.Closed;

    // 每次 Open 重建：旧连接上迟到的响应写入已完成的旧队列后被丢弃，不会串到新连接
    private Channel<byte[]> _rx = CreateRxChannel();

    // 部分读取后剩余的块。只在串行化的 I/O 路径（Read/Discard/Open）上访问
    private ReadOnlyMemory<byte> _pending;

    private Error? _nextOpenFault;
    private Error? _nextReadFault;
    private Error? _nextWriteFault;
    private Func<ReadOnlyMemory<byte>, byte[]?>? _responder;
    private long _openDelayTicks;
    private long _responseDelayTicks;
    private long _droppedChunks;

    public SimulatedTransport(BusId busId)
    {
        if (busId.IsEmpty)
            throw new ArgumentException("BusId 不能为空。", nameof(busId));

        BusId = busId;
    }

    public BusId BusId { get; }

    /// <summary>
    /// 模拟设备：收到请求后返回响应字节；返回 null 或空数组表示设备不应答（用于超时测试）。
    /// </summary>
    public Func<ReadOnlyMemory<byte>, byte[]?>? Responder
    {
        get => Volatile.Read(ref _responder);
        set => Volatile.Write(ref _responder, value);
    }

    /// <summary>
    /// 打开通道前的模拟延迟（用于连接超时测试），响应取消
    /// </summary>
    public TimeSpan OpenDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _openDelayTicks));
        set => Interlocked.Exchange(ref _openDelayTicks, value.Ticks);
    }

    /// <summary>
    /// 设备响应延迟（用于读超时与"超时后迟到数据"测试）
    /// </summary>
    public TimeSpan ResponseDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _responseDelayTicks));
        set => Interlocked.Exchange(ref _responseDelayTicks, value.Ticks);
    }

    /// <summary>
    /// 因接收队列满或所属连接已关闭而丢弃的数据块数
    /// </summary>
    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _state == LinkState.Open;
            }
        }
    }

    // ==================== IBusTransport ====================

    public async ValueTask<Result> OpenAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var delay = OpenDelay;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);

            if (_state == LinkState.Open)
                return Result.Failure(BusErrors.AlreadyOpen());

            if (Take(ref _nextOpenFault) is { } fault)
                return Result.Failure(fault);

            _rx = CreateRxChannel();
            _pending = ReadOnlyMemory<byte>.Empty;
            _state = LinkState.Open;
            return Result.Success();
        }
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CloseCore();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<Result<int>> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty)
            throw new ArgumentException("读缓冲区不能为空。", nameof(buffer));

        Channel<byte[]> rx;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);

            if (_state != LinkState.Open)
                return Result<int>.Failure(BusErrors.NotOpen());

            if (Take(ref _nextReadFault) is { } fault)
                return Result<int>.Failure(fault);

            rx = _rx;
        }

        if (_pending.IsEmpty)
        {
            if (!rx.Reader.TryRead(out var chunk))
            {
                try
                {
                    chunk = await rx.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    // 等待期间被 Close / DropConnection / Dispose：按约定立即以 ConnectionClosed 返回
                    return Result<int>.Failure(BusErrors.ConnectionClosed());
                }
            }

            _pending = chunk;
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending.Span[..count].CopyTo(buffer.Span);
        _pending = _pending[count..];
        return Result<int>.Success(count);
    }

    public ValueTask<Result> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Channel<byte[]> rx;
        CancellationToken lifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);

            if (_state != LinkState.Open)
                return ValueTask.FromResult(Result.Failure(BusErrors.NotOpen()));

            if (Take(ref _nextWriteFault) is { } fault)
                return ValueTask.FromResult(Result.Failure(fault));

            RecordTx(data.ToArray());
            rx = _rx;
            // 在锁内取 Token：Dispose 先 Cancel 再释放 CTS，锁外访问已释放 CTS 的 Token 会抛异常
            lifetime = _lifetime.Token;
        }

        var response = Responder?.Invoke(data);
        if (response is { Length: > 0 })
        {
            var delay = ResponseDelay;
            if (delay > TimeSpan.Zero)
                _ = EnqueueLaterAsync(rx, response, delay, lifetime);
            else
                Enqueue(rx, response);
        }

        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> DiscardInputAsync(CancellationToken cancellationToken)
    {
        Channel<byte[]> rx;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);

            if (_state != LinkState.Open)
                return ValueTask.FromResult(Result.Failure(BusErrors.NotOpen()));

            rx = _rx;
        }

        _pending = ReadOnlyMemory<byte>.Empty;
        while (rx.Reader.TryRead(out _))
        {
        }

        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state == LinkState.Disposed)
                return ValueTask.CompletedTask;

            CloseCore();
            _state = LinkState.Disposed;
        }

        // 取消所有尚未到达的延迟响应，防止仿真对象释放后仍有后台任务运行
        _lifetime.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    // ==================== 故障注入 ====================

    /// <summary>
    /// 下一次 Open 失败（一次性）
    /// </summary>
    public void FailNextOpen(Error? error = null)
    {
        lock (_gate)
        {
            _nextOpenFault = error ?? BusErrors.OpenFailed("仿真注入的打开失败。");
        }
    }

    /// <summary>
    /// 下一次 Read 失败（一次性）
    /// </summary>
    public void FailNextRead(Error? error = null)
    {
        lock (_gate)
        {
            _nextReadFault = error ?? BusErrors.IoFailed("仿真注入的读失败。");
        }
    }

    /// <summary>
    /// 下一次 Write 失败（一次性）
    /// </summary>
    public void FailNextWrite(Error? error = null)
    {
        lock (_gate)
        {
            _nextWriteFault = error ?? BusErrors.IoFailed("仿真注入的写失败。");
        }
    }

    /// <summary>
    /// 模拟对端断开（拔线、设备掉电）：通道关闭，正在等待的读操作立即返回 ConnectionClosed
    /// </summary>
    public void DropConnection()
    {
        lock (_gate)
        {
            CloseCore();
        }
    }

    /// <summary>
    /// 直接向接收端注入字节。通道未打开或接收队列已满时返回 false。
    /// </summary>
    public bool InjectRx(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return false;

        Channel<byte[]> rx;
        lock (_gate)
        {
            if (_state != LinkState.Open)
                return false;

            rx = _rx;
        }

        return Enqueue(rx, data.ToArray());
    }

    /// <summary>
    /// 最近写出的数据快照（最多 <see cref="TxHistoryCapacity"/> 条，旧的在前）
    /// </summary>
    public IReadOnlyList<byte[]> GetTxHistory()
    {
        lock (_gate)
        {
            return _txHistory.ToArray();
        }
    }

    // ==================== 内部 ====================

    private static Channel<byte[]> CreateRxChannel()
        => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(RxQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // 必须用 Wait：只有此模式下 TryWrite 在队列满时返回 false，溢出才能被计数；
            // Drop* 模式会静默丢数据且返回 true。写入方一律用 TryWrite，因此永远不会阻塞。
            FullMode = BoundedChannelFullMode.Wait,
        });

    // 调用方必须持有 _gate
    private void CloseCore()
    {
        if (_state != LinkState.Open)
            return;

        _state = LinkState.Closed;
        _rx.Writer.TryComplete();
    }

    // 调用方必须持有 _gate
    private void RecordTx(byte[] data)
    {
        if (_txHistory.Count == TxHistoryCapacity)
            _txHistory.Dequeue();

        _txHistory.Enqueue(data);
    }

    private bool Enqueue(Channel<byte[]> rx, byte[] chunk)
    {
        // TryWrite 不阻塞：队列满（溢出）或连接已关闭（迟到的旧数据）都直接丢弃并计数
        if (rx.Writer.TryWrite(chunk))
            return true;

        Interlocked.Increment(ref _droppedChunks);
        return false;
    }

    private async Task EnqueueLaterAsync(Channel<byte[]> rx, byte[] chunk, TimeSpan delay, CancellationToken lifetime)
    {
        try
        {
            await Task.Delay(delay, lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 仿真对象已释放，迟到响应没有接收方，按设计丢弃
            Interlocked.Increment(ref _droppedChunks);
            return;
        }

        Enqueue(rx, chunk);
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);
        }
    }

    private static Error? Take(ref Error? slot)
    {
        var value = slot;
        slot = null;
        return value;
    }
}
