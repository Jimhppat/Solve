using Microsoft.Extensions.Logging.Abstractions;
using Sample.DriverPackage;
using Solve.Adapters.DeviceLibrary;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Sqlite;
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
/// 报文交互执行器与轮询调度：真实总线运行时 + 仿真通道 + 示例驱动（虚构协议的示例电源），仿真设备按协议应答
/// </summary>
public sealed class DevicePollingTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly SwitchableTransportFactory _factory = new();
    private readonly ExchangeExecutor _executor = new(NullLogger<ExchangeExecutor>.Instance);
    private readonly SamplePowerDriver _driver = new();
    private SqliteDatabase _database = null!;
    private BusManager _manager = null!;
    private BusManagementService _buses = null!;

    private SqliteStorageOptions StorageOptions => new(Path.Combine(_directory, "solve.db"));

    public async Task InitializeAsync()
    {
        _database = (await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None)).Value;
        _manager = new BusManager(_factory, new TestInterlock(), NullLoggerFactory.Instance);
        _buses = new BusManagementService(_manager, new SqliteBusConfigurationRepository(_database, NullLogger.Instance),
            NullLogger<BusManagementService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        SqliteDatabase.ClearPool(StorageOptions);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // ---------- 仿真设备（示例协议） ----------

    private static byte Sum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        return (byte)sum;
    }

    /// <summary>
    /// 示例电源的仿真应答：读取返回固定的 12.34 V / 0.5 A，输出命令原样确认
    /// </summary>
    private static byte[]? SamplePowerResponder(ReadOnlyMemory<byte> request)
    {
        var r = request.Span;
        if (r.Length < 4 || r[0] != 0xAA)
            return null;

        byte[] response = r[2] == 0x01
            ? [0x55, r[1], 0x01, 0x04, 0xD2, 0x01, 0xF4, 0]
            : [0x55, r[1], r[2], r[3], 0];
        response[^1] = Sum(response.AsSpan(0, response.Length - 1));
        return response;
    }

    private async Task<(BusRuntime Runtime, SimulatedTransport Transport)> ConnectAsync(TimeSpan? readTimeout = null, int baudRate = 115_200)
    {
        var bus = new SerialBusDescriptor(BusId.New(), "RS485", "COM9")
        {
            BaudRate = baudRate,
            Timeouts = new BusTimeouts(TimeSpan.FromSeconds(1), readTimeout ?? TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300)),
        };
        var runtime = _manager.Register(bus).Value;
        Assert.True(_factory.TryGet(bus.Id, out var transport));
        Assert.True(runtime.Connect().IsSuccess);
        await WaitUntilAsync(() => runtime.State == BusState.Connected);
        return (runtime, transport);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
            await Task.Delay(20);

        Assert.True(condition(), "等待超时");
    }

    private IDeviceSession Session(int address = 1, int settleMs = 0)
        => _driver.CreateSession(new DeviceContext(address, LinkKind.Serial,
            new DeviceParameters(new Dictionary<string, string> { ["settle-ms"] = settleMs.ToString() })));

    // ---------- 执行器 ----------

    [Fact]
    public async Task Execute_RequestResponse_ReturnsSamples()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = SamplePowerResponder;

        var outcome = await _executor.ExecuteAsync(runtime, Session(address: 3).BeginPoll(), "测试", CancellationToken.None);

        var succeeded = Assert.IsType<ExchangeOutcome.Succeeded>(outcome);
        Assert.Equal([new PointSample("voltage", 12.34), new PointSample("current", 0.5)], succeeded.Samples);
        Assert.Equal(new byte[] { 0xAA, 3, 0x01, 0xAE }, transport.GetTxHistory()[^1]);
    }

    [Fact]
    public async Task Execute_NoResponse_FailsTransiently_AfterTimeout()
    {
        var (runtime, _) = await ConnectAsync(readTimeout: TimeSpan.FromMilliseconds(150));

        var outcome = await _executor.ExecuteAsync(runtime, Session().BeginPoll(), "测试", CancellationToken.None);

        var failed = Assert.IsType<ExchangeOutcome.DeviceFailed>(outcome);
        Assert.Equal(FailureKind.Transient, failed.Kind);
        Assert.Contains("响应超时", failed.Reason);
        Assert.Equal(BusState.Connected, runtime.State);
    }

    [Fact]
    public async Task Execute_ResponseInPieces_AndTrailingGarbage_IsAssembled()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = request =>
        {
            var full = SamplePowerResponder(request)!;

            // 先回半帧，其余在稍后分两次到达，末尾再夹带 2 个多余字节
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                transport.InjectRx(full.AsSpan(3, 3));
                await Task.Delay(30);
                transport.InjectRx([.. full.AsSpan(6), 0xFF, 0xFF]);
            });
            return full[..3];
        };

        var outcome = await _executor.ExecuteAsync(runtime, Session().BeginPoll(), "测试", CancellationToken.None);

        Assert.Equal(2, Assert.IsType<ExchangeOutcome.Succeeded>(outcome).Samples.Count);
    }

    [Fact]
    public async Task Execute_CorruptedChecksum_IsInvalid()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = request =>
        {
            var response = SamplePowerResponder(request)!;
            response[^1] ^= 0xFF;
            return response;
        };

        var outcome = await _executor.ExecuteAsync(runtime, Session().BeginPoll(), "测试", CancellationToken.None);

        Assert.Equal(FailureKind.Transient, Assert.IsType<ExchangeOutcome.DeviceFailed>(outcome).Kind);
    }

    [Fact]
    public async Task Execute_DriverConfigurationFailure_KeepsItsKind()
    {
        var (runtime, _) = await ConnectAsync();
        var exchange = Session().BeginCommand(new DeviceCommand("not.supported", new Dictionary<string, double>()));

        var outcome = await _executor.ExecuteAsync(runtime, exchange, "测试", CancellationToken.None);

        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeOutcome.DeviceFailed>(outcome).Kind);
    }

    [Fact]
    public async Task Execute_CommandWaitsDelayAfter()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = SamplePowerResponder;
        var exchange = Session(settleMs: 300).BeginCommand(new DeviceCommand("output.on", new Dictionary<string, double>()));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await _executor.ExecuteAsync(runtime, exchange, "测试", CancellationToken.None);

        Assert.IsType<ExchangeOutcome.Succeeded>(outcome);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(280), $"耗时 {watch.Elapsed}");
    }

    [Fact]
    public async Task Execute_BusNotConnected_IsBusUnavailable()
    {
        var bus = new SerialBusDescriptor(BusId.New(), "RS485", "COM8");
        var runtime = _manager.Register(bus).Value;

        var outcome = await _executor.ExecuteAsync(runtime, Session().BeginPoll(), "测试", CancellationToken.None);

        Assert.Equal(BusErrors.ChannelLockedCode, Assert.IsType<ExchangeOutcome.BusUnavailable>(outcome).Error.Code);
    }

    private sealed class ThrowingExchange : IExchange
    {
        public ExchangeStep Start() => new ExchangeStep.Send(new byte[] { 1 });

        public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength) => throw new InvalidOperationException("驱动缺陷");

        public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("不应到达");
    }

    private sealed class EndlessExchange : IExchange
    {
        public ExchangeStep Start() => new ExchangeStep.Send(new byte[] { 1 }, ExpectsResponse: false);

        public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength) => throw new InvalidOperationException("不应到达");

        public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => Start();
    }

    [Fact]
    public async Task Execute_DriverThrows_TripsBus_AndRethrows()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = _ => [0x55];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _executor.ExecuteAsync(runtime, new ThrowingExchange(), "测试", CancellationToken.None));

        await WaitUntilAsync(() => runtime.State == BusState.SafeStop);
    }

    [Fact]
    public async Task Execute_EndlessExchange_StopsAfterStepLimit()
    {
        var (runtime, _) = await ConnectAsync();

        var outcome = await _executor.ExecuteAsync(runtime, new EndlessExchange(), "测试", CancellationToken.None);

        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeOutcome.DeviceFailed>(outcome).Kind);
    }

    [Theory]
    [InlineData(9600, 3.646)]
    [InlineData(115_200, 1.75)]
    public void Gap_UsesCharacterTimeOrMinimum(int baud, double expectedMs)
    {
        var serial = new SerialBusDescriptor(BusId.New(), "RS485", "COM1") { BaudRate = baud };

        var gap = ExchangeExecutor.GapDuration(FrameGap.ModbusRtu, serial)!.Value;

        Assert.Equal(expectedMs, gap.TotalMilliseconds, precision: 3);
        Assert.Equal(TimeSpan.FromMilliseconds(1.75), ExchangeExecutor.GapDuration(FrameGap.ModbusRtu, new TcpBusDescriptor(BusId.New(), "TCP", "127.0.0.1", 502)));
    }

    // ---------- 通道级点位 ----------

    /// <summary>
    /// 通道号错位会把一路通道的读数显示到另一个位置上：只接受与声明相符的通道号
    /// </summary>
    [Theory]
    [InlineData("temperature", 0, true)]
    [InlineData("temperature", 1, false)]
    [InlineData("voltage", 1, true)]
    [InlineData("voltage", 16, true)]
    [InlineData("voltage", 0, false)]
    [InlineData("voltage", 17, false)]
    [InlineData("undeclared", 0, false)]
    public void ChannelSamples_MustMatchDeclaration(string key, int channel, bool accepted)
    {
        var driver = new DriverInfo("acme.daq-16", "采集卡", DeviceCategory.General, "", 1, 32)
        {
            Channels = [new ChannelGroupInfo("input", "输入", ChannelFunction.Measure, 16)],
            Points =
            [
                new PointInfo("temperature", "板温", "°C", PointAccess.Read),
                new PointInfo("voltage", "电压", "V", PointAccess.Read) { Group = "input" },
            ],
        };

        Assert.Equal(accepted, DevicePollingService.IsDeclared(driver, new PointSample(key, 1.0) { Channel = channel }));
    }

    // ---------- 轮询调度 ----------

    private sealed class FixedBindings(BusId busId, IReadOnlyList<DriverBinding> bindings) : IDeviceBindings
    {
        public IReadOnlyList<DriverBinding> Get(BusId id) => id == busId ? bindings : [];

        public int CountByDriver(DriverId driver) => bindings.Count(b => b.Device.Driver == driver);

        public bool TryGet(DeviceId device, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DriverBinding? binding)
        {
            binding = bindings.FirstOrDefault(b => b.Device.Id == device);
            return binding is not null;
        }

        public Task RefreshAsync(BusId id, CancellationToken cancellationToken) => Task.CompletedTask;

        public event EventHandler<BusId>? Changed { add { } remove { } }
    }

    private DriverBinding Bind(BusId busId, int address)
    {
        var info = _driver.Info;
        var catalog = new DriverCatalog(
            [.. DriverCatalog.BuiltIns, new DriverDescriptor(DriverId.Of(info.Id), info.Name, HardwareCategory.PowerAndLoad, "", info.MinAddress, info.MaxAddress)],
            new Dictionary<DriverId, IDeviceDriver> { [DriverId.Of(info.Id)] = _driver });
        var device = new DeviceDescriptor(DeviceId.New(), busId, DriverId.Of(info.Id), $"电源-{address}", address);
        return new DriverBinder(catalog, NullLogger<DriverBinder>.Instance).Bind(device, LinkKind.Serial);
    }

    private static readonly DevicePollingOptions FastPolling = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(50),
        FailureThreshold = 3,
        MaxBackoff = TimeSpan.FromMilliseconds(200),
        IdleRecheck = TimeSpan.FromMilliseconds(20),
    };

    private DevicePollingService CreatePolling(IDeviceBindings bindings, ModuleStatusStore store)
        => new(_buses, bindings, _executor, store, FastPolling, TimeProvider.System, NullLogger<DevicePollingService>.Instance);

    [Fact]
    public async Task Polling_ConnectedBus_ModuleGoesOnline_WithReadings()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = SamplePowerResponder;
        var binding = Bind(runtime.Id, 1);
        var store = new ModuleStatusStore();
        await using var polling = CreatePolling(new FixedBindings(runtime.Id, [binding]), store);

        polling.Start();

        await WaitUntilAsync(() => store.Get(binding.Device.Id)?.State == ModuleCommState.Online);
        var status = store.Get(binding.Device.Id)!;
        Assert.Equal([12.34, 0.5], status.Points.Select(p => p.Value));
        Assert.NotNull(status.LastSuccess);
    }

    [Fact]
    public async Task Polling_SilentDevice_BecomesCommFault_ButBusStaysConnected()
    {
        var (runtime, _) = await ConnectAsync(readTimeout: TimeSpan.FromMilliseconds(50));
        var binding = Bind(runtime.Id, 1);
        var store = new ModuleStatusStore();
        await using var polling = CreatePolling(new FixedBindings(runtime.Id, [binding]), store);

        polling.Start();

        await WaitUntilAsync(() => store.Get(binding.Device.Id)?.State == ModuleCommState.CommFault);
        Assert.Contains("响应超时", store.Get(binding.Device.Id)!.Problem);
        Assert.Equal(BusState.Connected, runtime.State);
    }

    [Fact]
    public async Task Polling_DisconnectedBus_ModuleWaits_AndNotPolledForUnreadyBinding()
    {
        var bus = new SerialBusDescriptor(BusId.New(), "RS485", "COM6");
        var runtime = _manager.Register(bus).Value;
        var ready = Bind(runtime.Id, 1);
        var outOfRange = Bind(runtime.Id, 99);
        var store = new ModuleStatusStore();
        await using var polling = CreatePolling(new FixedBindings(runtime.Id, [ready, outOfRange]), store);

        polling.Start();

        await WaitUntilAsync(() => store.Get(ready.Device.Id)?.State == ModuleCommState.Waiting
                                   && store.Get(ready.Device.Id)?.Problem == "总线未连接。");
        Assert.Equal(ModuleCommState.NotPolled, store.Get(outOfRange.Device.Id)!.State);
        Assert.Empty(_factory.TryGet(runtime.Id, out var transport) ? transport.GetTxHistory() : []);
    }

    [Fact]
    public async Task Polling_UnregisteredBus_StopsAndClearsStatuses()
    {
        var (runtime, transport) = await ConnectAsync();
        transport.Responder = SamplePowerResponder;
        var binding = Bind(runtime.Id, 1);
        var store = new ModuleStatusStore();
        await using var polling = CreatePolling(new FixedBindings(runtime.Id, [binding]), store);
        polling.Start();
        await WaitUntilAsync(() => store.Get(binding.Device.Id)?.State == ModuleCommState.Online);

        Assert.True((await _manager.UnregisterAsync(runtime.Id, CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => store.Get(binding.Device.Id) is null);
    }
}
