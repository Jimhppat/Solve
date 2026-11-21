using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Sample.DriverPackage;
using Solve.Adapters.DeviceLibrary;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Devices;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Application.Devices.Simulation;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Tests.Buses.TestSupport;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.Devices;

/// <summary>
/// 仿真模式下的设备：驱动提供的仿真设备经分派接到仿真通道上，模块能经轮询上线、执行命令
/// </summary>
public sealed class DeviceSimulationTests : IAsyncLifetime
{
    // ==================== 测试驱动 ====================

    private delegate byte[]? ReplyFunc(byte address, ReadOnlySpan<byte> request);

    /// <summary>
    /// 测试协议：请求 [地址]，应答 [地址, 01]；仿真设备按 <paramref name="reply"/> 应答（默认只应答发给自己地址的请求）
    /// </summary>
    private sealed class EchoDriver(string id, ReplyFunc? reply = null, Func<DeviceContext, ISimulatedDevice?>? create = null)
        : IDeviceDriver, IDeviceSimulatorProvider
    {
        private readonly ReplyFunc _reply = reply ?? DefaultReply;

        public int Created { get; private set; }

        public List<Device> Devices { get; } = [];

        public DriverInfo Info { get; } = new(id, "回显设备", DeviceCategory.General, "", 1, 32);

        public IDeviceSession CreateSession(DeviceContext context) => new EchoSession((byte)context.Address);

        public ISimulatedDevice CreateSimulator(DeviceContext context)
        {
            Created++;
            if (create is not null)
                return create(context)!;

            var device = new Device(_reply, (byte)context.Address);
            Devices.Add(device);
            return device;
        }

        private static byte[]? DefaultReply(byte address, ReadOnlySpan<byte> request)
            => request.Length > 0 && request[0] == address ? [address, 0x01] : null;

        public sealed class Device(ReplyFunc reply, byte address) : ISimulatedDevice
        {
            public int Requests { get; private set; }

            public byte[]? Respond(ReadOnlySpan<byte> request)
            {
                Requests++;
                return reply(address, request);
            }
        }
    }

    private sealed class EchoSession(byte address) : IDeviceSession
    {
        public IExchange BeginPoll() => new EchoExchange(address);

        public IExchange BeginCommand(DeviceCommand command) => new EchoExchange(address);
    }

    private sealed class EchoExchange(byte address) : IExchange
    {
        public ExchangeStep Start() => new ExchangeStep.Send(new[] { address });

        public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
        {
            frameLength = received.Length >= 2 ? 2 : 0;
            if (received.Length >= 1 && received[0] != address)
                return FrameStatus.Invalid;
            return received.Length >= 2 ? FrameStatus.Complete : FrameStatus.NeedMoreData;
        }

        public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => new ExchangeStep.Completed([]);
    }

    /// <summary>
    /// 不提供仿真的驱动
    /// </summary>
    private sealed class PlainDriver : IDeviceDriver
    {
        public DriverInfo Info { get; } = new("test.plain", "无仿真设备", DeviceCategory.General, "", 1, 32);

        public IDeviceSession CreateSession(DeviceContext context) => new EchoSession((byte)context.Address);
    }

    private sealed class MutableBindings(BusId busId) : IDeviceBindings
    {
        public List<DriverBinding> Bindings { get; set; } = [];

        public IReadOnlyList<DriverBinding> Get(BusId id) => id == busId ? Bindings.ToList() : [];

        public int CountByDriver(DriverId driver) => Bindings.Count(b => b.Device.Driver == driver);

        public bool TryGet(DeviceId device, [NotNullWhen(true)] out DriverBinding? binding)
        {
            binding = Bindings.FirstOrDefault(b => b.Device.Id == device);
            return binding is not null;
        }

        public Task RefreshAsync(BusId id, CancellationToken cancellationToken) => Task.CompletedTask;

        public event EventHandler<BusId>? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, busId);
    }

    private static DriverBinding Bind(IDeviceDriver driver, BusId busId, int address)
    {
        var info = driver.Info;
        var id = DriverId.Of(info.Id);
        var catalog = new DriverCatalog(
            [.. DriverCatalog.BuiltIns, new DriverDescriptor(id, info.Name, HardwareCategory.General, "", info.MinAddress, info.MaxAddress)],
            new Dictionary<DriverId, IDeviceDriver> { [id] = driver });
        var device = new DeviceDescriptor(DeviceId.New(), busId, id, $"模块-{address}", address);
        return new DriverBinder(catalog, NullLogger<DriverBinder>.Instance).Bind(device, LinkKind.Serial);
    }

    private static (SimulatedDeviceDispatcher Dispatcher, MutableBindings Bindings, BusId Bus) CreateDispatcher()
    {
        var bus = BusId.New();
        var bindings = new MutableBindings(bus);
        var dispatcher = new SimulatedDeviceDispatcher(NullLogger<SimulatedDeviceDispatcher>.Instance);
        dispatcher.Attach(bindings);
        return (dispatcher, bindings, bus);
    }

    // ==================== 分派 ====================

    [Fact]
    public void BeforeAttach_NoResponse()
    {
        using var dispatcher = new SimulatedDeviceDispatcher(NullLogger<SimulatedDeviceDispatcher>.Instance);

        Assert.Null(dispatcher.Respond(BusId.New(), [1]));
    }

    [Fact]
    public void Attach_Twice_Throws()
    {
        var (dispatcher, bindings, _) = CreateDispatcher();

        Assert.Throws<InvalidOperationException>(() => dispatcher.Attach(bindings));
    }

    [Fact]
    public void Request_IsAnsweredOnlyByAddressedDevice()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var driver = new EchoDriver("test.echo");
        bindings.Bindings = [Bind(driver, bus, 1), Bind(driver, bus, 2)];

        Assert.Equal(new byte[] { 2, 0x01 }, dispatcher.Respond(bus, [2, 0xFF]));
        Assert.Equal(new byte[] { 1, 0x01 }, dispatcher.Respond(bus, [1, 0xFF]));
        Assert.Null(dispatcher.Respond(bus, [9, 0xFF]));
        Assert.Null(dispatcher.Respond(BusId.New(), [1, 0xFF]));
    }

    [Fact]
    public void DriverWithoutSimulator_AndUnreadyBinding_AreSkipped()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var echo = new EchoDriver("test.echo");
        var outOfRange = Bind(echo, bus, 99);
        Assert.Equal(DriverBindingStatus.AddressOutOfRange, outOfRange.Status);
        bindings.Bindings = [Bind(new PlainDriver(), bus, 1), outOfRange];

        Assert.Null(dispatcher.Respond(bus, [1]));
        Assert.Null(dispatcher.Respond(bus, [99]));
        Assert.Equal(0, echo.Created);
    }

    [Fact]
    public void TwoDevicesAnswering_IsCollision_NoResponse_ButBothSeeTheRequest()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var greedy = new EchoDriver("test.greedy", (address, _) => [address]);
        bindings.Bindings = [Bind(greedy, bus, 1), Bind(greedy, bus, 2)];

        Assert.Null(dispatcher.Respond(bus, [1]));
        Assert.Equal([1, 1], greedy.Devices.Select(d => d.Requests));

        // 冲突消除后恢复正常应答
        bindings.Bindings = [bindings.Bindings[0]];
        Assert.Equal(new byte[] { 1 }, dispatcher.Respond(bus, [1]));
    }

    [Fact]
    public void SimulatorThrows_OnlyThisRequestUnanswered_OthersUnaffected()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var failNext = true;
        var flaky = new EchoDriver("test.flaky", (address, request) =>
        {
            if (failNext)
                throw new InvalidOperationException("仿真缺陷");
            return request[0] == address ? [address] : null;
        });
        var healthy = new EchoDriver("test.echo");
        bindings.Bindings = [Bind(flaky, bus, 1), Bind(healthy, bus, 2)];

        Assert.Null(dispatcher.Respond(bus, [1]));
        Assert.Equal(new byte[] { 2, 0x01 }, dispatcher.Respond(bus, [2]));

        failNext = false;
        Assert.Equal(new byte[] { 1 }, dispatcher.Respond(bus, [1]));
    }

    [Fact]
    public void CreateSimulatorThrowsOrReturnsNull_ModuleSilent_NotRetriedForSameBinding()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var throwing = new EchoDriver("test.throwing", create: _ => throw new InvalidOperationException("创建失败"));
        var returningNull = new EchoDriver("test.null", create: _ => null);
        bindings.Bindings = [Bind(throwing, bus, 1), Bind(returningNull, bus, 2)];

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(dispatcher.Respond(bus, [1]));
            Assert.Null(dispatcher.Respond(bus, [2]));
        }

        Assert.Equal(1, throwing.Created);
        Assert.Equal(1, returningNull.Created);
    }

    [Fact]
    public void OversizedResponse_IsDropped()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var huge = new EchoDriver("test.huge", (_, _) => new byte[ISimulatedDevice.MaxResponseLength + 1]);
        bindings.Bindings = [Bind(huge, bus, 1)];

        Assert.Null(dispatcher.Respond(bus, [1]));
    }

    [Fact]
    public void Response_IsCopied_DriverBufferReuseDoesNotLeak()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var buffer = new byte[] { 0x11, 0x22 };
        var reusing = new EchoDriver("test.reuse", (_, _) => buffer);
        bindings.Bindings = [Bind(reusing, bus, 1)];

        var response = dispatcher.Respond(bus, [1])!;
        buffer[0] = 0xEE;

        Assert.Equal(new byte[] { 0x11, 0x22 }, response);
    }

    [Fact]
    public void SameBinding_KeepsSimulatorState_ReplacedBinding_StartsOver()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var power = new SamplePowerDriver();
        var binding = Bind(power, bus, 1);
        bindings.Bindings = [binding];

        Assert.NotNull(dispatcher.Respond(bus, SampleFrame(1, 0x10, 1)));
        Assert.Equal(12.0, SampleVoltage(dispatcher.Respond(bus, SampleFrame(1, 0x01))!));

        // 模块配置变化后换了新绑定：仿真设备重建，输出回到上电默认的关闭
        bindings.Bindings = [Bind(power, bus, 1)];
        bindings.RaiseChanged();
        Assert.Equal(0.0, SampleVoltage(dispatcher.Respond(bus, SampleFrame(1, 0x01))!));
    }

    [Fact]
    public void BindingsChanged_DropsRemovedModules_AndDisposeDetaches()
    {
        var (dispatcher, bindings, bus) = CreateDispatcher();
        var echo = new EchoDriver("test.echo");
        bindings.Bindings = [Bind(echo, bus, 1)];
        Assert.NotNull(dispatcher.Respond(bus, [1]));

        bindings.Bindings = [];
        bindings.RaiseChanged();
        Assert.Null(dispatcher.Respond(bus, [1]));

        dispatcher.Dispose();
        bindings.RaiseChanged();
    }

    // ==================== 示例电源的仿真设备 ====================

    private static byte Sum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        return (byte)sum;
    }

    private static byte[] SampleFrame(byte address, byte function, params byte[] payload)
    {
        byte[] frame = [0xAA, address, function, .. payload, 0];
        frame[^1] = Sum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    private static double SampleVoltage(byte[] response) => (response[3] << 8 | response[4]) / 100.0;

    [Fact]
    public void SamplePowerSimulator_AnswersPollAndOutput_RemembersState()
    {
        var simulator = new SamplePowerDriver.Simulator(3);

        var off = simulator.Respond(SampleFrame(3, 0x01))!;
        Assert.Equal(8, off.Length);
        Assert.Equal(0.0, SampleVoltage(off));

        Assert.Equal(new byte[] { 0x55, 3, 0x10, 1, Sum([0x55, 3, 0x10, 1]) }, simulator.Respond(SampleFrame(3, 0x10, 1)));
        Assert.True(simulator.OutputOn);

        var on = simulator.Respond(SampleFrame(3, 0x01))!;
        Assert.Equal(SamplePowerDriver.Simulator.SimulatedVoltage, SampleVoltage(on));
        Assert.Equal(SamplePowerDriver.Simulator.SimulatedCurrent, (on[5] << 8 | on[6]) / 1000.0);
        Assert.Equal(Sum(on.AsSpan(0, 7)), on[7]);

        Assert.NotNull(simulator.Respond(SampleFrame(3, 0x10, 0)));
        Assert.False(simulator.OutputOn);
    }

    [Fact]
    public void SamplePowerSimulator_IgnoresOtherAddress_BadChecksum_UnknownFunction()
    {
        var simulator = new SamplePowerDriver.Simulator(3);
        var badChecksum = SampleFrame(3, 0x01);
        badChecksum[^1] ^= 0xFF;

        Assert.Null(simulator.Respond(SampleFrame(4, 0x01)));
        Assert.Null(simulator.Respond(badChecksum));
        Assert.Null(simulator.Respond(SampleFrame(3, 0x7F)));
        Assert.Null(simulator.Respond(SampleFrame(3, 0x10, 2)));
        Assert.Null(simulator.Respond([0xAA]));
        Assert.False(simulator.OutputOn);
    }

    // ==================== 回环：真实总线运行时 + 仿真通道 + 绑定登记表 + 轮询 ====================

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly SimulatedDeviceDispatcher _dispatcher = new(NullLogger<SimulatedDeviceDispatcher>.Instance);
    private readonly ExchangeExecutor _executor = new(NullLogger<ExchangeExecutor>.Instance);
    private readonly ModuleStatusStore _statuses = new();
    private SimulatedTransportFactory _factory = null!;
    private SqliteDatabase _database = null!;
    private BusManager _manager = null!;
    private BusManagementService _buses = null!;
    private SqliteDeviceRepository _devices = null!;
    private DeviceBindingRegistry _registry = null!;

    private SqliteStorageOptions StorageOptions => new(Path.Combine(_directory, "solve.db"));

    private static readonly DevicePollingOptions FastPolling = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(50),
        FailureThreshold = 3,
        MaxBackoff = TimeSpan.FromMilliseconds(200),
        IdleRecheck = TimeSpan.FromMilliseconds(20),
    };

    public async Task InitializeAsync()
    {
        _database = (await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None)).Value;

        // 与组合根的仿真模式相同的接法：工厂为每条总线的仿真通道接上分派
        _factory = new SimulatedTransportFactory(busId => request => _dispatcher.Respond(busId, request.Span));
        _manager = new BusManager(_factory, new TestInterlock(), NullLoggerFactory.Instance);
        _buses = new BusManagementService(_manager, new SqliteBusConfigurationRepository(_database, NullLogger.Instance),
            NullLogger<BusManagementService>.Instance);
        _devices = new SqliteDeviceRepository(_database, NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_registry is not null)
            await _registry.DisposeAsync();
        _dispatcher.Dispose();
        await _manager.DisposeAsync();
        SqliteDatabase.ClearPool(StorageOptions);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task<DevicePollingService> StartAsync(params IDeviceDriver[] drivers)
    {
        var descriptors = drivers.Select(d => new DriverDescriptor(DriverId.Of(d.Info.Id), d.Info.Name, HardwareCategory.PowerAndLoad, "", d.Info.MinAddress, d.Info.MaxAddress));
        var catalog = new DriverCatalog([.. DriverCatalog.BuiltIns, .. descriptors], drivers.ToDictionary(d => DriverId.Of(d.Info.Id)));
        _registry = new DeviceBindingRegistry(_devices, _buses, new DriverBinder(catalog, NullLogger<DriverBinder>.Instance),
            NullLogger<DeviceBindingRegistry>.Instance);
        _dispatcher.Attach(_registry);
        await _registry.StartAsync(CancellationToken.None);

        var polling = new DevicePollingService(_buses, _registry, _executor, _statuses, FastPolling, TimeProvider.System,
            NullLogger<DevicePollingService>.Instance);
        polling.Start();
        return polling;
    }

    private async Task<BusRuntime> CreateConnectedBusAsync()
    {
        var bus = new SerialBusDescriptor(BusId.New(), "RS485", "COM9")
        {
            BaudRate = 115_200,
            Timeouts = new BusTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)),
        };
        Assert.True((await _buses.CreateBusAsync(bus, CancellationToken.None)).IsSuccess);
        var runtime = _buses.GetRuntime(bus.Id).Value;
        Assert.True(runtime.Connect().IsSuccess);
        await WaitUntilAsync(() => runtime.State == BusState.Connected);
        return runtime;
    }

    private async Task<DeviceDescriptor> AddModuleAsync(BusId busId, IDeviceDriver driver, int address)
    {
        var device = new DeviceDescriptor(DeviceId.New(), busId, DriverId.Of(driver.Info.Id), $"电源-{address}", address);
        Assert.True((await _devices.AddRangeAsync([device], CancellationToken.None)).IsSuccess);
        await _registry.RefreshAsync(busId, CancellationToken.None);
        return device;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
            await Task.Delay(20);

        Assert.True(condition(), "等待超时");
    }

    private double? Reading(DeviceId device, string key)
        => _statuses.Get(device)?.Points.Where(p => p.Key == key).Select(p => (double?)p.Value).FirstOrDefault();

    [Fact]
    public async Task Loopback_SamplePowerModules_GoOnline_AndOutputCommandChangesReadings()
    {
        var driver = new SamplePowerDriver();
        await using var polling = await StartAsync(driver);
        var runtime = await CreateConnectedBusAsync();
        var first = await AddModuleAsync(runtime.Id, driver, 1);
        var second = await AddModuleAsync(runtime.Id, driver, 2);

        await WaitUntilAsync(() => _statuses.Get(first.Id)?.State == ModuleCommState.Online
                                   && _statuses.Get(second.Id)?.State == ModuleCommState.Online);
        Assert.Equal(0.0, Reading(first.Id, SamplePowerDriver.VoltageKey));

        // 命令走执行器（与命令服务同一路径），由仿真设备确认并记住输出状态
        Assert.True(_registry.TryGet(first.Id, out var binding));
        var command = binding.Session!.BeginCommand(new DeviceCommand(SamplePowerDriver.OutputOnKey, new Dictionary<string, double>()));
        Assert.IsType<ExchangeOutcome.Succeeded>(await _executor.ExecuteAsync(runtime, command, "测试", CancellationToken.None));

        await WaitUntilAsync(() => Reading(first.Id, SamplePowerDriver.VoltageKey) is SamplePowerDriver.Simulator.SimulatedVoltage);
        Assert.Equal(SamplePowerDriver.Simulator.SimulatedCurrent, Reading(first.Id, SamplePowerDriver.CurrentKey));
        Assert.Equal(0.0, Reading(second.Id, SamplePowerDriver.VoltageKey));
        Assert.Equal(BusState.Connected, runtime.State);
    }

    [Fact]
    public async Task Loopback_ThrowingSimulator_OnlyItsModuleFaults_BusAndNeighbourStayHealthy()
    {
        var sample = new SamplePowerDriver();
        var broken = new EchoDriver("test.broken", (_, _) => throw new InvalidOperationException("仿真缺陷"));
        await using var polling = await StartAsync(sample, broken);
        var runtime = await CreateConnectedBusAsync();
        var healthy = await AddModuleAsync(runtime.Id, sample, 1);
        var faulty = await AddModuleAsync(runtime.Id, broken, 2);

        // 仿真设备每次都抛异常：该模块只会响应超时转为通信异常；总线保持连接，同总线的示例电源照常在线
        await WaitUntilAsync(() => _statuses.Get(healthy.Id)?.State == ModuleCommState.Online
                                   && _statuses.Get(faulty.Id)?.State == ModuleCommState.CommFault);
        Assert.Contains("响应超时", _statuses.Get(faulty.Id)!.Problem);
        Assert.Equal(BusState.Connected, runtime.State);
        Assert.Equal(ModuleCommState.Online, _statuses.Get(healthy.Id)!.State);
    }

    [Fact]
    public async Task Loopback_WithoutDispatcher_ModuleNeverOnline()
    {
        // 对照：通道没有接分派（旧行为 / 实机工厂不存在分派），模块只会超时
        var plainFactory = new SwitchableTransportFactory();
        await using var manager = new BusManager(plainFactory, new TestInterlock(), NullLoggerFactory.Instance);
        var bus = new SerialBusDescriptor(BusId.New(), "RS485", "COM9")
        {
            Timeouts = new BusTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300)),
        };
        var runtime = manager.Register(bus).Value;
        Assert.True(runtime.Connect().IsSuccess);
        await WaitUntilAsync(() => runtime.State == BusState.Connected);
        var session = new SamplePowerDriver().CreateSession(new DeviceContext(1, LinkKind.Serial,
            new DeviceParameters(new Dictionary<string, string> { [SamplePowerDriver.SettleTimeKey] = "0" })));

        var outcome = await _executor.ExecuteAsync(runtime, session.BeginPoll(), "测试", CancellationToken.None);

        Assert.Contains("响应超时", Assert.IsType<ExchangeOutcome.DeviceFailed>(outcome).Reason);
    }
}
