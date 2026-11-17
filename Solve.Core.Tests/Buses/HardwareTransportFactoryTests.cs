using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses;
using Solve.Adapters.Hardware.Buses.Serial;
using Solve.Adapters.Hardware.Buses.Tcp;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses;

/// <summary>
/// 工厂只创建未打开的通道，不做任何 I/O，因此可安全使用任意端口名/地址
/// </summary>
public sealed class HardwareTransportFactoryTests
{
    private sealed record UnknownBusDescriptor(BusId Id, string Name) : BusDescriptor(Id, Name)
    {
        protected override void CollectLinkErrors(List<Error> errors)
        {
        }
    }

    private readonly HardwareTransportFactory _factory = new(NullLoggerFactory.Instance);

    [Fact]
    public async Task Serial_CreatesClosedSerialPortTransport()
    {
        var result = _factory.Create(new SerialBusDescriptor(BusId.New(), "RS485", "COM3"));

        await using var transport = result.Value;
        Assert.IsType<SerialPortTransport>(transport);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task Tcp_CreatesClosedTcpTransport()
    {
        var result = _factory.Create(new TcpBusDescriptor(BusId.New(), "PLC", "192.168.1.10", 502));

        await using var transport = result.Value;
        Assert.IsType<TcpTransport>(transport);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void InvalidDescriptor_IsRejected()
    {
        var result = _factory.Create(new TcpBusDescriptor(BusId.New(), "PLC", "bad host", 502));

        Assert.Equal(BusErrors.InvalidDescriptorCode, result.Error.Code);
    }

    [Fact]
    public void UnsupportedDescriptor_IsRejected()
    {
        var result = _factory.Create(new UnknownBusDescriptor(BusId.New(), "CAN-1"));

        Assert.Equal(BusErrors.UnsupportedDescriptorCode, result.Error.Code);
    }
}
