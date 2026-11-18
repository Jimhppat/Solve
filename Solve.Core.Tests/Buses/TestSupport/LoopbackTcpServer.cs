using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Solve.Core.Tests.Buses.TestSupport;

/// <summary>
/// 仅监听 127.0.0.1 随机端口的回环测试服务器：默认原样回显，可主动断开客户端或停止监听。
/// 不访问任何外部网络或设备。
/// </summary>
internal sealed class LoopbackTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Socket> _clients = new();
    private readonly Task _acceptLoop;
    private int _accepted;
    private volatile bool _echo = true;

    public LoopbackTcpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public int AcceptedCount => Volatile.Read(ref _accepted);

    /// <summary>
    /// false 时只收不回，模拟设备不应答
    /// </summary>
    public bool Echo
    {
        get => _echo;
        set => _echo = value;
    }

    /// <summary>
    /// 取得一个当前未被占用的回环端口（监听后立即关闭），用于"连接被拒绝"测试
    /// </summary>
    public static int GetClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task WaitForClientsAsync(int count, TimeSpan timeout)
    {
        using var guard = new CancellationTokenSource(timeout);
        while (AcceptedCount < count)
            await Task.Delay(5, guard.Token);
    }

    /// <summary>
    /// 主动断开所有已接入客户端（模拟设备重启 / 对端关闭）
    /// </summary>
    public void DropClients()
    {
        while (_clients.TryDequeue(out var client))
        {
            client.Shutdown(SocketShutdown.Both);
            client.Dispose();
        }
    }

    public async Task SendToAllAsync(byte[] data)
    {
        foreach (var client in _clients)
            await client.SendAsync(data, SocketFlags.None);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        while (_clients.TryDequeue(out var client))
            client.Dispose();

        await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptSocketAsync(_stop.Token);
                _clients.Enqueue(client);
                Interlocked.Increment(ref _accepted);
                _ = EchoLoopAsync(client);
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 服务器停止：Listener 关闭导致 Accept 中止，属预期退出路径
        }
    }

    private async Task EchoLoopAsync(Socket client)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var count = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (count == 0)
                    return;

                if (Echo)
                    await client.SendAsync(buffer.AsMemory(0, count), SocketFlags.None, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 客户端断开或服务器停止：该连接的回显循环结束，属预期退出路径
        }
    }
}
