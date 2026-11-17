using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Core.Tests.Buses.TestSupport;

namespace Solve.Core.Tests.Buses;

public sealed class BusManagerTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private readonly SimulatedTransportFactory _factory = new();
    private readonly TestInterlock _interlock = new();
    private readonly BusManager _manager;

    public BusManagerTests()
    {
        _manager = new BusManager(_factory, _interlock, NullLoggerFactory.Instance, new BusRuntimeOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
    }

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    private static SerialBusDescriptor Serial(string port) => new(BusId.New(), $"RS485-{port}", port);

    private static TcpBusDescriptor Tcp() => new(BusId.New(), "PLC", "127.0.0.1", 502);

    private static async Task WaitForStateAsync(BusRuntime runtime, BusState expected)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (runtime.State != expected)
            await Task.Delay(5, guard.Token);
    }

    [Fact]
    public void Register_AddsDisconnectedRuntime()
    {
        var descriptor = Tcp();

        var result = _manager.Register(descriptor);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(BusState.Disconnected, result.Value.State);
        Assert.True(_manager.TryGet(descriptor.Id, out var found));
        Assert.Same(result.Value, found);
        Assert.Single(_manager.Buses);
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
        var descriptor = Tcp();
        _manager.Register(descriptor);

        var result = _manager.Register(descriptor with { Name = "另一个名字" });

        Assert.Equal(BusErrors.AlreadyRegisteredCode, result.Error.Code);
        Assert.Single(_manager.Buses);
    }

    [Theory]
    [InlineData("COM3", "COM3")]
    [InlineData("COM3", "com3")]
    [InlineData("COM3", " COM3 ")]
    public void SameSerialPort_IsRejected(string first, string second)
    {
        Assert.True(_manager.Register(Serial(first)).IsSuccess);

        var result = _manager.Register(Serial(second));

        Assert.Equal(BusErrors.ResourceConflictCode, result.Error.Code);
    }

    [Fact]
    public void DifferentSerialPorts_AndSameTcpEndpoint_AreAllowed()
    {
        Assert.True(_manager.Register(Serial("COM3")).IsSuccess);
        Assert.True(_manager.Register(Serial("COM4")).IsSuccess);
        Assert.True(_manager.Register(Tcp()).IsSuccess);
        Assert.True(_manager.Register(Tcp()).IsSuccess);

        Assert.Equal(4, _manager.Buses.Count);
    }

    [Fact]
    public void InvalidDescriptor_IsRejected_WithoutThrowing()
    {
        var result = _manager.Register(new SerialBusDescriptor(BusId.New(), "RS485", null!));

        Assert.Equal(BusErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Empty(_manager.Buses);
    }

    [Fact]
    public async Task Unregister_DisconnectsAndReleasesTransport()
    {
        var descriptor = Tcp();
        var runtime = _manager.Register(descriptor).Value;
        Assert.True(_factory.TryGet(descriptor.Id, out var sim));
        runtime.Connect();
        await WaitForStateAsync(runtime, BusState.Connected);

        var result = await _manager.UnregisterAsync(descriptor.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(sim.IsOpen);
        Assert.False(_manager.TryGet(descriptor.Id, out _));
        Assert.Throws<ObjectDisposedException>(() => runtime.Connect());
    }

    [Fact]
    public async Task Unregister_FromSafeStop_StillReleases()
    {
        var descriptor = Tcp();
        var runtime = _manager.Register(descriptor).Value;
        Assert.True(_factory.TryGet(descriptor.Id, out var sim));
        runtime.Connect();
        await WaitForStateAsync(runtime, BusState.Connected);
        runtime.SafetyTrip("测试");

        Assert.True((await _manager.UnregisterAsync(descriptor.Id, CancellationToken.None)).IsSuccess);
        Assert.False(sim.IsOpen);
    }

    [Fact]
    public async Task UnregisterUnknown_ReturnsNotRegistered()
    {
        var result = await _manager.UnregisterAsync(BusId.New(), CancellationToken.None);

        Assert.Equal(BusErrors.NotRegisteredCode, result.Error.Code);
    }

    [Fact]
    public async Task ReleasedSerialPort_CanBeRegisteredAgain()
    {
        var first = Serial("COM7");
        _manager.Register(first);
        await _manager.UnregisterAsync(first.Id, CancellationToken.None);

        Assert.True(_manager.Register(Serial("COM7")).IsSuccess);
    }

    [Fact]
    public async Task SafetyTripAll_LatchesEveryBus()
    {
        var a = _manager.Register(Tcp()).Value;
        var b = _manager.Register(Serial("COM9")).Value;
        a.Connect();
        await WaitForStateAsync(a, BusState.Connected);

        Assert.Equal(2, _manager.SafetyTripAll("系统急停"));

        Assert.Equal(BusState.SafeStop, a.State);
        Assert.Equal(BusState.SafeStop, b.State);
    }

    [Fact]
    public async Task Dispose_ReleasesAllBuses_AndRejectsRegistration()
    {
        var descriptor = Tcp();
        var runtime = _manager.Register(descriptor).Value;
        Assert.True(_factory.TryGet(descriptor.Id, out var sim));
        runtime.Connect();
        await WaitForStateAsync(runtime, BusState.Connected);

        await _manager.DisposeAsync();
        await _manager.DisposeAsync();

        Assert.False(sim.IsOpen);
        Assert.Empty(_manager.Buses);
        Assert.Throws<ObjectDisposedException>(() => _manager.Register(Tcp()));
    }
}
