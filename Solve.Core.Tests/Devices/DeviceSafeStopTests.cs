using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Tests.Buses.TestSupport;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.Devices;

/// <summary>
/// 总线安全跳闸 → 设备层安全停止：真实总线注册表与运行时 + 仿真通道 + 假会话（虚构的一问一答"安全"报文 A5 地址 / 5A 地址）
/// </summary>
public sealed class DeviceSafeStopTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private readonly SwitchableTransportFactory _factory = new();
    private BusManager _manager;

    public DeviceSafeStopTests()
    {
        _manager = CreateManager(TimeSpan.FromSeconds(2));
    }

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    private BusManager CreateManager(TimeSpan safeStopTimeout) => new(_factory, new TestInterlock(), NullLoggerFactory.Instance, new BusRuntimeOptions
    {
        ShutdownTimeout = TimeSpan.FromSeconds(2),
        SafeStopTimeout = safeStopTimeout,
    });

    // ---------- 假驱动 ----------

    private enum Behavior
    {
        Answers,
        Silent,
        Throws,
        NoCapability,
    }

    private sealed class SafeExchange(int address) : IExchange
    {
        public ExchangeStep Start() => new ExchangeStep.Send(new byte[] { 0xA5, (byte)address }, ResponseTimeout: TimeSpan.FromMilliseconds(150));

        public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
        {
            frameLength = 0;
            if (received.Length < 2)
                return FrameStatus.NeedMoreData;
            if (received[0] != 0x5A || received[1] != address)
                return FrameStatus.Invalid;
            frameLength = 2;
            return FrameStatus.Complete;
        }

        public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => new ExchangeStep.Completed([]);
    }

    private class PlainSession : IDeviceSession
    {
        public IExchange BeginPoll() => throw new NotSupportedException();

        public IExchange BeginCommand(DeviceCommand command) => throw new NotSupportedException();
    }

    private sealed class SafeSession(int address, bool throws) : PlainSession, ISafeStateProvider
    {
        public IExchange BeginSafeState(string? group, int channel) => throws ? throw new InvalidOperationException("驱动缺陷") : new SafeExchange(address);
    }

    private sealed class FixedBindings(BusId busId, IReadOnlyList<DriverBinding> bindings) : IDeviceBindings
    {
        public IReadOnlyList<DriverBinding> Get(BusId id) => id == busId ? bindings : [];

        public int CountByDriver(DriverId driver) => 0;

        public bool TryGet(DeviceId device, [NotNullWhen(true)] out DriverBinding? binding)
        {
            binding = bindings.FirstOrDefault(b => b.Device.Id == device);
            return binding is not null;
        }

        public Task RefreshAsync(BusId id, CancellationToken cancellationToken) => Task.CompletedTask;

        public event EventHandler<BusId>? Changed { add { } remove { } }
    }

    private static DriverBinding Bind(BusId busId, int address, Behavior behavior)
    {
        IDeviceSession session = behavior == Behavior.NoCapability
            ? new PlainSession()
            : new SafeSession(address, behavior == Behavior.Throws);
        var device = new DeviceDescriptor(DeviceId.New(), busId, DriverId.Of("test.load"), $"负载-{address}", address);
        return new DriverBinding(device, DriverBindingStatus.Ready, null) { Session = session };
    }

    /// <summary>
    /// 注册并连上一条总线，按 <paramref name="modules"/>（地址 → 行为）挂上设备层安全停止处理
    /// </summary>
    private async Task<(BusRuntime Runtime, SimulatedTransport Transport)> SetUpAsync(params (int Address, Behavior Behavior)[] modules)
    {
        var bus = new TcpBusDescriptor(BusId.New(), "负载总线", "127.0.0.1", 502);
        var runtime = _manager.Register(bus).Value;
        Assert.True(_factory.TryGet(bus.Id, out var transport));

        var silent = modules.Where(m => m.Behavior == Behavior.Silent).Select(m => (byte)m.Address).ToHashSet();
        transport.Responder = request => request.Span is [0xA5, var address] && !silent.Contains(address) ? [0x5A, address] : null;

        var bindings = new FixedBindings(bus.Id, modules.Select(m => Bind(bus.Id, m.Address, m.Behavior)).ToList());
        _manager.AttachSafeStopHandler(new DeviceSafeStopHandler(
            bindings, new ExchangeExecutor(NullLogger<ExchangeExecutor>.Instance), NullLogger<DeviceSafeStopHandler>.Instance));

        Assert.True(runtime.Connect().IsSuccess);
        using var guard = new CancellationTokenSource(Guard);
        while (runtime.State != BusState.Connected)
            await Task.Delay(5, guard.Token);
        return (runtime, transport);
    }

    private static IEnumerable<int> SafeFramesSent(SimulatedTransport transport)
        => transport.GetTxHistory().Where(f => f is [0xA5, _]).Select(f => (int)f[1]);

    // ---------- 设备层 ----------

    [Fact]
    public async Task TripAll_SendsSafeState_ToEveryCapableModule_InAddressOrder_EvenAfterFailures()
    {
        var (_, transport) = await SetUpAsync(
            (1, Behavior.Answers),
            (2, Behavior.Silent),
            (3, Behavior.Throws),
            (4, Behavior.NoCapability),
            (5, Behavior.Answers));

        var tripped = await _manager.SafetyTripAllAsync("操作员急停", CancellationToken.None).WaitAsync(Guard);

        Assert.Equal(1, tripped);

        // 2 不应答、3 的驱动抛异常、4 没有该能力：都不影响 5
        Assert.Equal([1, 2, 5], SafeFramesSent(transport));
        using var guard = new CancellationTokenSource(Guard);
        while (transport.IsOpen)
            await Task.Delay(5, guard.Token);
    }

    [Fact]
    public async Task SlowModules_DeadlineCutsOff_TripStillCompletes()
    {
        await _manager.DisposeAsync();
        _manager = CreateManager(TimeSpan.FromMilliseconds(400));

        // 每个不应答的模块占用 150 ms 响应超时：400 ms 内最多轮到 3 个
        var (runtime, transport) = await SetUpAsync(
            (1, Behavior.Silent), (2, Behavior.Silent), (3, Behavior.Silent), (4, Behavior.Silent), (5, Behavior.Silent), (6, Behavior.Silent));

        var watch = Stopwatch.StartNew();
        runtime.SafetyTrip("急停");
        await runtime.SafeStopSettled.WaitAsync(Guard);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"安全停止耗时 {watch.Elapsed}");
        var sent = SafeFramesSent(transport).ToList();
        Assert.InRange(sent.Count, 1, 4);
        Assert.DoesNotContain(6, sent);
        using var guard = new CancellationTokenSource(Guard);
        while (transport.IsOpen)
            await Task.Delay(5, guard.Token);
    }

    // ---------- 挂接 ----------

    [Fact]
    public void AttachTwice_Throws()
    {
        var handler = new DeviceSafeStopHandler(
            new FixedBindings(BusId.New(), []), new ExchangeExecutor(NullLogger<ExchangeExecutor>.Instance), NullLogger<DeviceSafeStopHandler>.Instance);
        _manager.AttachSafeStopHandler(handler);

        Assert.Throws<InvalidOperationException>(() => _manager.AttachSafeStopHandler(handler));
    }

    [Fact]
    public async Task WithoutHandler_TripOnlyClosesChannel()
    {
        var bus = new TcpBusDescriptor(BusId.New(), "负载总线", "127.0.0.1", 502);
        var runtime = _manager.Register(bus).Value;
        Assert.True(_factory.TryGet(bus.Id, out var transport));
        Assert.True(runtime.Connect().IsSuccess);
        using var guard = new CancellationTokenSource(Guard);
        while (runtime.State != BusState.Connected)
            await Task.Delay(5, guard.Token);

        await _manager.SafetyTripAllAsync("急停", CancellationToken.None).WaitAsync(Guard);

        Assert.Empty(transport.GetTxHistory());
        while (transport.IsOpen)
            await Task.Delay(5, guard.Token);
    }
}
