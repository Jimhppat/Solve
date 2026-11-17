using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses;

public sealed class SimulatedTransportFactoryTests
{
    private sealed record UnknownBusDescriptor(BusId Id, string Name) : BusDescriptor(Id, Name)
    {
        protected override void CollectLinkErrors(List<Error> errors)
        {
        }
    }

    private readonly SimulatedTransportFactory _factory = new();

    [Fact]
    public async Task ValidDescriptor_CreatesClosedSimulatedTransport_AndCanBeLookedUp()
    {
        var descriptor = new TcpBusDescriptor(BusId.New(), "PLC-01", "127.0.0.1", 502);

        var result = _factory.Create(descriptor);

        Assert.True(result.IsSuccess);
        await using var transport = result.Value;
        Assert.False(transport.IsOpen);
        Assert.True(_factory.TryGet(descriptor.Id, out var simulated));
        Assert.Same(transport, simulated);
    }

    [Fact]
    public void InvalidDescriptor_IsRejected()
    {
        var descriptor = new SerialBusDescriptor(BusId.New(), "RS485", "") { BaudRate = 0 };

        var result = _factory.Create(descriptor);

        Assert.Equal(BusErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Contains("Bus.Descriptor.PortName", result.Error.Message);
        Assert.Contains("Bus.Descriptor.BaudRate", result.Error.Message);
        Assert.False(_factory.TryGet(descriptor.Id, out _));
    }

    [Fact]
    public void UnsupportedDescriptor_IsRejected()
    {
        var result = _factory.Create(new UnknownBusDescriptor(BusId.New(), "CAN-1"));

        Assert.Equal(BusErrors.UnsupportedDescriptorCode, result.Error.Code);
    }
}
