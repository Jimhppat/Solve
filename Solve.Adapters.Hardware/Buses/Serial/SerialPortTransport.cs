using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Hardware.Buses.Serial;

/// <summary>
/// 串口（RS-232 / RS-485）物理通道。
/// </summary>
/// <remarks>
/// <para>
/// 不使用 SerialPort.BaseStream.ReadAsync：其在 Windows 上不响应 CancellationToken，读超时时只能关闭端口才能解除阻塞，
/// 会把"设备没应答"放大成"链路重连"。改为：DataReceived 事件唤醒 + 兜底轮询，然后只同步读取已到达的字节（不会阻塞）。
/// 等待阶段完全可取消，单次读超时不影响链路。
/// </para>
/// <para>
/// 写操作同步执行于线程池，由 SerialPort.WriteTimeout（= 描述中的写超时）保证有界；
/// 写入开始后取消令牌不再生效，最长阻塞一个写超时。
/// </para>
/// </remarks>
public sealed class SerialPortTransport : IBusTransport
{
    /// <summary>
    /// 兜底轮询间隔：部分 USB 转串口驱动会丢失 DataReceived 事件，到期后主动检查 BytesToRead
    /// </summary>
    public static readonly TimeSpan FallbackPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// 同步读的安全上限。只在已有数据时才读，正常情况立即返回；该值只防止驱动异常导致无限阻塞
    /// </summary>
    private const int SyncReadTimeoutMilliseconds = 500;

    private enum LinkState
    {
        Closed,
        Open,
        Disposed,
    }

    private readonly SerialBusDescriptor _descriptor;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private LinkState _state = LinkState.Closed;
    private SerialPort? _port;
    private Channel<bool>? _dataSignal;
    private SerialDataReceivedEventHandler? _dataHandler;

    public SerialPortTransport(SerialBusDescriptor descriptor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(logger);
        _descriptor = descriptor;
        _logger = logger;
    }

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

    public async ValueTask<Result> OpenAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);
            if (_state == LinkState.Open)
                return Result.Failure(BusErrors.AlreadyOpen());
        }

        SerialPort port;
        try
        {
            port = CreatePort();
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(BusErrors.OpenFailed($"{_descriptor.PortName} 参数无效：{ex.Message}"));
        }

        // SerialPort.Open 是同步调用，部分 USB 驱动会卡住数秒：放到线程池并让等待可取消
        var openTask = Task.Run(port.Open, CancellationToken.None);
        try
        {
            await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            // 丢弃打开前驱动缓冲里的陈旧数据（放在 try 内：驱动报错时同样要释放句柄）
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch (OperationCanceledException)
        {
            // 放弃等待不等于放弃打开：Open 稍后完成时必须释放句柄，否则端口会被本进程一直占用
            _ = openTask.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((SerialPort)state!).Dispose();
                },
                port,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception ex) when (IsPortFault(ex) || ex is ArgumentException)
        {
            port.Dispose();
            return Result.Failure(BusErrors.OpenFailed($"{_descriptor.PortName} {ex.GetType().Name}：{ex.Message}"));
        }

        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        SerialDataReceivedEventHandler handler = (_, _) => signal.Writer.TryWrite(true);

        lock (_gate)
        {
            if (_state == LinkState.Disposed)
            {
                port.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }

            port.DataReceived += handler;
            _port = port;
            _dataSignal = signal;
            _dataHandler = handler;
            _state = LinkState.Open;
        }

        _logger.LogInformation("串口已打开 {Port} {Baud} {DataBits}{Parity}{StopBits}",
            _descriptor.PortName, _descriptor.BaudRate, _descriptor.DataBits, _descriptor.Parity, _descriptor.StopBits);
        return Result.Success();
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        CloseCore(LinkState.Closed);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Result<int>> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty)
            throw new ArgumentException("读缓冲区不能为空。", nameof(buffer));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetPort(out var port, out var signal, out var notOpen))
                return Result<int>.Failure(notOpen);

            try
            {
                var available = port.BytesToRead;
                if (available > 0)
                {
                    var count = port.BaseStream.Read(buffer.Span[..Math.Min(available, buffer.Length)]);
                    if (count > 0)
                        return Result<int>.Success(count);
                }
            }
            catch (Exception ex) when (IsPortFault(ex))
            {
                return Result<int>.Failure(MapIoFault("Read", ex));
            }

            // 等待数据到达事件；兜底定时检查防止驱动丢事件。两者都随令牌取消
            var dataArrived = signal.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(dataArrived, Task.Delay(FallbackPollInterval, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (completed == dataArrived)
            {
                // 信号队列被完成 = 端口已被 Close / Dispose
                if (!await dataArrived.ConfigureAwait(false))
                    return Result<int>.Failure(BusErrors.ConnectionClosed());

                signal.Reader.TryRead(out _);
            }
        }
    }

    public async ValueTask<Result> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetPort(out var port, out _, out var notOpen))
            return Result.Failure(notOpen);

        var segment = MemoryMarshal.TryGetArray(data, out var array)
            ? array
            : new ArraySegment<byte>(data.ToArray());

        try
        {
            await Task.Run(() => port.Write(segment.Array!, segment.Offset, segment.Count), CancellationToken.None)
                .ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (IsPortFault(ex))
        {
            return Result.Failure(MapIoFault("Write", ex, data));
        }
    }

    public ValueTask<Result> DiscardInputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetPort(out var port, out var signal, out var notOpen))
            return ValueTask.FromResult(Result.Failure(notOpen));

        try
        {
            port.DiscardInBuffer();
            signal.Reader.TryRead(out _);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (IsPortFault(ex))
        {
            return ValueTask.FromResult(Result.Failure(MapIoFault("DiscardInput", ex)));
        }
    }

    public ValueTask DisposeAsync()
    {
        CloseCore(LinkState.Disposed);
        return ValueTask.CompletedTask;
    }

    // ==================== 内部 ====================

    private SerialPort CreatePort() => new(
        _descriptor.PortName,
        _descriptor.BaudRate,
        SerialMappings.ToPortParity(_descriptor.Parity),
        _descriptor.DataBits,
        SerialMappings.ToPortStopBits(_descriptor.StopBits))
    {
        Handshake = Handshake.None,
        ReadTimeout = SyncReadTimeoutMilliseconds,
        WriteTimeout = (int)Math.Ceiling(_descriptor.Timeouts.Write.TotalMilliseconds),
    };

    private bool TryGetPort(out SerialPort port, out Channel<bool> signal, out Error notOpen)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);

            if (_state == LinkState.Open && _port is not null && _dataSignal is not null)
            {
                port = _port;
                signal = _dataSignal;
                notOpen = null!;
                return true;
            }
        }

        port = null!;
        signal = null!;
        notOpen = BusErrors.NotOpen();
        return false;
    }

    /// <summary>
    /// SerialPort 在端口被关闭、USB 拔出、驱动错误时抛出的异常类型
    /// </summary>
    private static bool IsPortFault(Exception ex)
        => ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or TimeoutException or ObjectDisposedException;

    private Error MapIoFault(string operation, Exception ex, ReadOnlyMemory<byte> packet = default)
    {
        // 本端已关闭（Close / Dispose 与 I/O 竞争）不是故障
        if (!IsOpen)
            return BusErrors.ConnectionClosed();

        _logger.LogWarning("串口 {Port} {Operation} 失败 {Type}：{Message} 报文={Packet}",
            _descriptor.PortName, operation, ex.GetType().Name, ex.Message,
            packet.IsEmpty ? "-" : Convert.ToHexString(packet.Span[..Math.Min(packet.Length, 256)]));

        return ex is TimeoutException
            ? BusErrors.IoFailed($"{_descriptor.PortName} {operation} 超时（流控阻塞或硬件故障）")
            : BusErrors.IoFailed($"{_descriptor.PortName} {operation} {ex.GetType().Name}：{ex.Message}");
    }

    private void CloseCore(LinkState target)
    {
        SerialPort? port;
        Channel<bool>? signal;
        SerialDataReceivedEventHandler? handler;

        lock (_gate)
        {
            if (_state == LinkState.Disposed)
                return;

            var wasOpen = _state == LinkState.Open;
            _state = target;
            port = wasOpen ? _port : null;
            signal = wasOpen ? _dataSignal : null;
            handler = wasOpen ? _dataHandler : null;
            _port = null;
            _dataSignal = null;
            _dataHandler = null;
        }

        if (port is null)
            return;

        // 先完成信号队列：等待中的 ReadAsync 立即以 ConnectionClosed 返回
        signal?.Writer.TryComplete();
        port.DataReceived -= handler;

        try
        {
            port.Dispose();
            _logger.LogInformation("串口已关闭 {Port}", _descriptor.PortName);
        }
        catch (IOException ex)
        {
            // USB 转串口在设备已拔出时关闭句柄可能报 IOException；句柄已失效，记录后视为已关闭
            _logger.LogWarning(ex, "串口 {Port} 关闭时驱动报错（设备可能已拔出），句柄视为已释放", _descriptor.PortName);
        }
    }
}
