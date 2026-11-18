using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Hardware.Buses.Tcp;

/// <summary>
/// TCP 客户端物理通道（上位机主动连接 PLC / 串口服务器）。
/// </summary>
/// <remarks>
/// 使用 Socket 原生异步 I/O，取消令牌可真正中止挂起的收发。
/// 开启 TCP KeepAlive：对端掉电/拔网线时 Socket 不会收到 FIN，没有协议心跳的情况下靠 KeepAlive 发现半开连接，
/// 检测时间约为 KeepAliveTime + KeepAliveInterval × RetryCount = 10 + 2 × 3 = 16 s。
/// </remarks>
public sealed class TcpTransport : IBusTransport
{
    private const int KeepAliveTimeSeconds = 10;
    private const int KeepAliveIntervalSeconds = 2;
    private const int KeepAliveRetryCount = 3;

    private enum LinkState
    {
        Closed,
        Open,
        Disposed,
    }

    private readonly TcpBusDescriptor _descriptor;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    // DiscardInput 的丢弃缓冲。I/O 由调用方串行化，可复用，避免每次分配
    private readonly byte[] _discardBuffer = new byte[512];

    private LinkState _state = LinkState.Closed;
    private Socket? _socket;

    public TcpTransport(TcpBusDescriptor descriptor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(logger);
        _descriptor = descriptor;
        _logger = logger;
    }

    private string Endpoint => $"{_descriptor.Host}:{_descriptor.Port}";

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

        // 双栈 Socket：同时支持 IPv4 / IPv6 地址与主机名
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            ConfigureKeepAlive(socket);
            await socket.ConnectAsync(_descriptor.Host, _descriptor.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            return Result.Failure(BusErrors.OpenFailed($"{Endpoint} {ex.SocketErrorCode}：{ex.Message}"));
        }

        lock (_gate)
        {
            if (_state == LinkState.Disposed)
            {
                // 连接期间被释放：不保留刚建立的连接
                socket.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }

            _socket = socket;
            _state = LinkState.Open;
        }

        _logger.LogInformation("TCP 已连接 {Endpoint}（本地 {Local}）", Endpoint, socket.LocalEndPoint);
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

        if (!TryGetSocket(out var socket, out var notOpen))
            return Result<int>.Failure(notOpen);

        try
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            // 0 字节表示对端已发送 FIN（正常关闭），按端口约定转为 ConnectionClosed 而不是返回 0
            return count > 0
                ? Result<int>.Success(count)
                : Result<int>.Failure(BusErrors.ConnectionClosed());
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return Result<int>.Failure(MapIoFault("Read", ex));
        }
    }

    public async ValueTask<Result> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!TryGetSocket(out var socket, out var notOpen))
            return Result.Failure(notOpen);

        try
        {
            // 发送缓冲区紧张时 SendAsync 可能只发出一部分，循环直到全部写出
            while (!data.IsEmpty)
            {
                var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                data = data[sent..];
            }

            return Result.Success();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return Result.Failure(MapIoFault("Write", ex));
        }
    }

    public ValueTask<Result> DiscardInputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSocket(out var socket, out var notOpen))
            return ValueTask.FromResult(Result.Failure(notOpen));

        try
        {
            // 只读取内核缓冲区中已到达的字节：Available > 0 时同步 Receive 不会阻塞
            var discarded = 0;
            int available;
            while ((available = socket.Available) > 0)
                discarded += socket.Receive(_discardBuffer, Math.Min(available, _discardBuffer.Length), SocketFlags.None);

            if (discarded > 0)
                _logger.LogDebug("TCP {Endpoint} 丢弃残留数据 {Count} B", Endpoint, discarded);

            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return ValueTask.FromResult(Result.Failure(MapIoFault("DiscardInput", ex)));
        }
    }

    public ValueTask DisposeAsync()
    {
        CloseCore(LinkState.Disposed);
        return ValueTask.CompletedTask;
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetryCount);
    }

    private bool TryGetSocket(out Socket socket, out Error notOpen)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LinkState.Disposed, this);

            if (_state == LinkState.Open && _socket is not null)
            {
                socket = _socket;
                notOpen = null!;
                return true;
            }
        }

        socket = null!;
        notOpen = BusErrors.NotOpen();
        return false;
    }

    private Error MapIoFault(string operation, Exception ex)
    {
        // 本端已关闭导致的中止（Close / Dispose 唤醒挂起的读）不是故障，按约定报告 ConnectionClosed
        if (!IsOpen)
            return BusErrors.ConnectionClosed();

        if (ex is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown })
            return BusErrors.ConnectionClosed();

        var detail = ex is SocketException se ? $"{se.SocketErrorCode}：{se.Message}" : ex.Message;
        _logger.LogWarning("TCP {Endpoint} {Operation} 失败：{Detail}", Endpoint, operation, detail);
        return BusErrors.IoFailed($"{Endpoint} {operation} {detail}");
    }

    private void CloseCore(LinkState target)
    {
        Socket? socket;
        lock (_gate)
        {
            if (_state == LinkState.Disposed)
                return;

            var wasOpen = _state == LinkState.Open;
            socket = _socket;
            _socket = null;
            _state = target;

            if (!wasOpen)
                socket = null;
        }

        if (socket is null)
            return;

        // Dispose 会中止该 Socket 上挂起的收发，等待中的 ReadAsync 随即以 ConnectionClosed 返回
        socket.Dispose();
        _logger.LogInformation("TCP 已关闭 {Endpoint}", Endpoint);
    }
}
