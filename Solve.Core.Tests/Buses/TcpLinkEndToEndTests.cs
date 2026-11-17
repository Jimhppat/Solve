using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.Buses.TestSupport;

namespace Solve.Core.Tests.Buses;

/// <summary>
/// 真实 TcpTransport + BusRuntime + BusManager 的端到端链路测试，对端为本机回环服务器
/// </summary>
public sealed class TcpLinkEndToEndTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static readonly byte[] Request = [0x01, 0x03, 0x00, 0x00, 0x00, 0x02];

    private readonly LoopbackTcpServer _server = new();
    private readonly BusManager _manager = new(
        new HardwareTransportFactory(NullLoggerFactory.Instance),
        new TestInterlock(),
        NullLoggerFactory.Instance);

    private readonly BusRuntime _runtime;

    public TcpLinkEndToEndTests()
    {
        var descriptor = new TcpBusDescriptor(BusId.New(), "Loopback-PLC", "127.0.0.1", _server.Port)
        {
            Reconnect = new ReconnectPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), 2.0, 5),
        };
        _runtime = _manager.Register(descriptor).Value;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task WaitForStateAsync(BusState expected)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (_runtime.State != expected)
            await Task.Delay(5, guard.Token);
    }

    private static async ValueTask<Result<byte[]>> EchoAsync(IBusChannel channel, CancellationToken ct)
    {
        var discard = await channel.DiscardInputAsync(ct);
        if (discard.IsFailure)
            return Result<byte[]>.Failure(discard.Error);

        var write = await channel.WriteAsync(Request, ct);
        if (write.IsFailure)
            return Result<byte[]>.Failure(write.Error);

        // 流式语义：循环读到期望长度（真实协议层会按帧头/长度字段做帧同步）
        var received = new byte[Request.Length];
        var offset = 0;
        while (offset < received.Length)
        {
            var read = await channel.ReadAsync(received.AsMemory(offset), ct);
            if (read.IsFailure)
                return Result<byte[]>.Failure(read.Error);
            offset += read.Value;
        }

        return Result<byte[]>.Success(received);
    }

    [Fact]
    public async Task ConnectExecuteDisconnect_OverRealTcp()
    {
        Assert.True(_runtime.Connect().IsSuccess);
        await WaitForStateAsync(BusState.Connected);

        var result = await _runtime.ExecuteAsync(EchoAsync, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Request, result.Value);

        Assert.True((await _runtime.DisconnectAsync(CancellationToken.None)).IsSuccess);
        Assert.Equal(BusState.Disconnected, _runtime.State);
    }

    [Fact]
    public async Task DeviceSilent_ReturnsTimeout_LinkStaysConnected()
    {
        _server.Echo = false;
        _runtime.Connect();
        await WaitForStateAsync(BusState.Connected);

        var result = await _runtime.ExecuteAsync(EchoAsync, CancellationToken.None);

        Assert.Equal(BusErrors.TimeoutCode, result.Error.Code);
        Assert.Equal(BusState.Connected, _runtime.State);

        _server.Echo = true;
        Assert.True((await _runtime.ExecuteAsync(EchoAsync, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task PeerDrop_IsDetected_AndLinkRecovers()
    {
        _runtime.Connect();
        await WaitForStateAsync(BusState.Connected);
        await _server.WaitForClientsAsync(1, Guard);

        _server.DropClients();
        var failed = await _runtime.ExecuteAsync(EchoAsync, CancellationToken.None);

        Assert.True(failed.IsFailure);
        await _server.WaitForClientsAsync(2, Guard);
        await WaitForStateAsync(BusState.Connected);
        Assert.True((await _runtime.ExecuteAsync(EchoAsync, CancellationToken.None)).IsSuccess);
    }
}
