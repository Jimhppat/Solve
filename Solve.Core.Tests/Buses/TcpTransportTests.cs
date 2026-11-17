using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Tcp;
using Solve.Core.Domain.Buses;
using Solve.Core.Tests.Buses.TestSupport;

namespace Solve.Core.Tests.Buses;

/// <summary>
/// TCP 通道测试，只连接本机回环服务器
/// </summary>
public sealed class TcpTransportTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private readonly LoopbackTcpServer _server = new();
    private readonly TcpTransport _transport;

    public TcpTransportTests()
    {
        _transport = Create(_server.Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static TcpTransport Create(int port)
        => new(new TcpBusDescriptor(BusId.New(), "Loopback", "127.0.0.1", port), NullLogger<TcpTransport>.Instance);

    private static CancellationTokenSource GuardSource() => new(Guard);

    private async Task OpenAsync()
    {
        using var guard = GuardSource();
        var result = await _transport.OpenAsync(guard.Token);
        Assert.True(result.IsSuccess, result.ToString());
        await _server.WaitForClientsAsync(1, Guard);
    }

    private async Task<byte[]> ReadAtLeastAsync(int count)
    {
        using var guard = GuardSource();
        var received = new List<byte>();
        var buffer = new byte[64];
        while (received.Count < count)
        {
            var result = await _transport.ReadAsync(buffer, guard.Token);
            Assert.True(result.IsSuccess, result.ToString());
            received.AddRange(buffer[..result.Value]);
        }

        return [.. received];
    }

    [Fact]
    public async Task Open_Echo_Close()
    {
        await OpenAsync();
        Assert.True(_transport.IsOpen);

        using var guard = GuardSource();
        Assert.True((await _transport.WriteAsync(new byte[] { 0x01, 0x03, 0x00, 0x10 }, guard.Token)).IsSuccess);
        Assert.Equal([0x01, 0x03, 0x00, 0x10], await ReadAtLeastAsync(4));

        await _transport.CloseAsync(guard.Token);
        await _transport.CloseAsync(guard.Token);
        Assert.False(_transport.IsOpen);
    }

    [Fact]
    public async Task OpenTwice_ReturnsAlreadyOpen_AndIoBeforeOpen_ReturnsNotOpen()
    {
        using var guard = GuardSource();
        Assert.Equal(BusErrors.NotOpenCode, (await _transport.WriteAsync(new byte[] { 1 }, guard.Token)).Error.Code);
        Assert.Equal(BusErrors.NotOpenCode, (await _transport.ReadAsync(new byte[1], guard.Token)).Error.Code);

        await OpenAsync();
        Assert.Equal(BusErrors.AlreadyOpenCode, (await _transport.OpenAsync(guard.Token)).Error.Code);
    }

    [Fact]
    public async Task ConnectionRefused_ReturnsOpenFailed()
    {
        await using var transport = Create(LoopbackTcpServer.GetClosedPort());
        using var guard = GuardSource();

        var result = await transport.OpenAsync(guard.Token);

        Assert.Equal(BusErrors.OpenFailedCode, result.Error.Code);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task RemoteClose_ReadReturnsConnectionClosed()
    {
        await OpenAsync();

        _server.DropClients();

        using var guard = GuardSource();
        var result = await _transport.ReadAsync(new byte[16], guard.Token);
        Assert.Equal(BusErrors.ConnectionClosedCode, result.Error.Code);
    }

    [Fact]
    public async Task LocalClose_WakesPendingRead()
    {
        await OpenAsync();
        using var guard = GuardSource();
        var pending = _transport.ReadAsync(new byte[16], guard.Token).AsTask();

        await _transport.CloseAsync(guard.Token);

        var result = await pending.WaitAsync(Guard);
        Assert.Equal(BusErrors.ConnectionClosedCode, result.Error.Code);
    }

    [Fact]
    public async Task ReadCancellation_Throws_AndLinkStaysUsable()
    {
        // 单次读超时（设备没应答）不得破坏链路，否则每次超时都会被放大为重连
        _server.Echo = false;
        await OpenAsync();

        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _transport.ReadAsync(new byte[16], timeout.Token).AsTask());
        }

        Assert.True(_transport.IsOpen);
        _server.Echo = true;
        using var guard = GuardSource();
        Assert.True((await _transport.WriteAsync(new byte[] { 0x7E }, guard.Token)).IsSuccess);
        Assert.Equal([0x7E], await ReadAtLeastAsync(1));
    }

    [Fact]
    public async Task DiscardInput_DropsBytesAlreadyReceived()
    {
        await OpenAsync();
        await _server.SendToAllAsync([0xDE, 0xAD, 0xBE, 0xEF]);
        await Task.Delay(100); // 等数据进入本端内核接收缓冲

        using var guard = GuardSource();
        Assert.True((await _transport.DiscardInputAsync(guard.Token)).IsSuccess);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _transport.ReadAsync(new byte[16], timeout.Token).AsTask());
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndRejectsFurtherUse()
    {
        await OpenAsync();

        await _transport.DisposeAsync();
        await _transport.DisposeAsync();

        Assert.False(_transport.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _transport.OpenAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _transport.ReadAsync(new byte[1], CancellationToken.None).AsTask());
    }
}
