using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sample.DriverPackage;
using Solve.Adapters.DeviceLibrary;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Devices;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Adapters.Persistence.Topology;
using Solve.Adapters.Persistence.Wiring;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Buses.TestSupport;
using Solve.Device.Sdk;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.Devices;

/// <summary>
/// 按位置下发设备命令：真实的总线运行时、绑定登记表、接线服务与 SQLite 临时库，仿真通道上挂仿真设备按协议应答（回环），不访问硬件。
/// 拓扑 1 柜 × 2 层 × 2 槽 × 1 治具 × 2 产品，每个层位下 4 个产品位。
/// </summary>
public sealed class DeviceCommandServiceTests : IAsyncLifetime
{
    private static readonly DriverId SamplePower = DriverId.Of("sample.power");
    private static readonly DriverId Relay = DriverId.Of(TestRelayDriver.Id);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly SwitchableTransportFactory _factory = new();
    private readonly SamplePowerDriver _sample = new();
    private readonly TestRelayDriver _relay = new();
    private readonly ExchangeExecutor _executor = new(NullLogger<ExchangeExecutor>.Instance);
    private readonly CapturingLogger<DeviceCommandService> _log = new();
    private SqliteDatabase _database = null!;
    private BusManager _manager = null!;
    private BusManagementService _buses = null!;
    private DeviceBindingRegistry _bindings = null!;
    private TopologyService _topology = null!;
    private WiringService _wiring = null!;
    private ModuleStatusStore _statuses = null!;
    private DeviceService _devices = null!;
    private WiringInspector _inspector = null!;
    private DevicePollingService? _polling;
    private BusRuntime _runtime = null!;
    private SimulatedTransport _transport = null!;

    private SqliteStorageOptions StorageOptions => new(Path.Combine(_directory, "solve.db"));

    public async Task InitializeAsync()
    {
        _database = (await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None)).Value;
        _manager = new BusManager(_factory, new TestInterlock(), NullLoggerFactory.Instance);
        _buses = new BusManagementService(_manager, new SqliteBusConfigurationRepository(_database, NullLogger.Instance),
            NullLogger<BusManagementService>.Instance);

        var catalog = new DriverCatalog(
            [
                .. DriverCatalog.BuiltIns,
                new DriverDescriptor(SamplePower, _sample.Info.Name, HardwareCategory.PowerAndLoad, "", 1, 32)
                {
                    Channels = [new ChannelGroup(SamplePowerDriver.OutputChannelKey, "输出", ChannelKind.PowerSupply, 1)],
                },
                new DriverDescriptor(Relay, _relay.Info.Name, HardwareCategory.General, "", 1, 32)
                {
                    Channels = [new ChannelGroup(TestRelayDriver.Group, "继电器", ChannelKind.Switch, 4)],
                },
            ],
            new Dictionary<DriverId, IDeviceDriver> { [SamplePower] = _sample, [Relay] = _relay });

        var deviceRepository = new SqliteDeviceRepository(_database, NullLogger.Instance);
        _bindings = new DeviceBindingRegistry(deviceRepository, _buses,
            new DriverBinder(catalog, NullLogger<DriverBinder>.Instance), NullLogger<DeviceBindingRegistry>.Instance);
        _topology = new TopologyService(new SqliteTopologyRepository(_database, NullLogger.Instance), NullLogger<TopologyService>.Instance);
        _wiring = new WiringService(new SqliteWiringRepository(_database, NullLogger.Instance), _topology, _bindings, catalog, _buses,
            NullLogger<WiringService>.Instance);
        _statuses = new ModuleStatusStore();
        _devices = new DeviceService(deviceRepository, catalog, _bindings, _statuses, NullLogger<DeviceService>.Instance, _wiring);
        _inspector = new WiringInspector(_wiring, _bindings, catalog, _statuses, _buses);

        var plan = _topology.PlanLayout("老化房", new TopologyLayout(1, 2, 2, 1, 2)).Value;
        Assert.True((await _topology.SaveAsync(plan, CancellationToken.None)).IsSuccess);

        var bus = new SerialBusDescriptor(BusId.New(), "1# RS-485", "COM7")
        {
            BaudRate = 115_200,
            Timeouts = new BusTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)),
        };
        _runtime = (await _buses.CreateBusAsync(bus, CancellationToken.None)).Value;
        Assert.True(_factory.TryGet(bus.Id, out _transport));
        _transport.Responder = Respond;
        Assert.True(_runtime.Connect().IsSuccess);
        await WaitUntilAsync(() => _runtime.State == BusState.Connected);

        await _bindings.StartAsync(CancellationToken.None);
        Assert.True((await _wiring.LoadAsync(CancellationToken.None)).IsSuccess);
        _wiring.Start();
    }

    public async Task DisposeAsync()
    {
        if (_polling is not null)
            await _polling.DisposeAsync();
        await _wiring.DisposeAsync();
        await _bindings.DisposeAsync();
        await _manager.DisposeAsync();
        SqliteDatabase.ClearPool(StorageOptions);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // ==================== 仿真设备 ====================

    /// <summary>
    /// 继电器板应答故障码（设备报告自身故障）
    /// </summary>
    private volatile bool _relayFaults;

    private byte[]? Respond(ReadOnlyMemory<byte> request)
    {
        var r = request.Span;
        if (r.Length == 4 && r[0] == TestRelayDriver.RequestHeader)
            return _relayFaults ? [TestRelayDriver.ResponseHeader, r[1], TestRelayDriver.FaultCode, 0x07] : [TestRelayDriver.ResponseHeader, r[1], r[2], r[3]];

        if (r.Length < 4 || r[0] != 0xAA)
            return null;

        // 示例电源：读取返回 12.34 V / 0.5 A，输出命令原样确认
        byte[] response = r[2] == 0x01
            ? [0x55, r[1], 0x01, 0x04, 0xD2, 0x01, 0xF4, 0]
            : [0x55, r[1], r[2], r[3], 0];
        response[^1] = Sum(response.AsSpan(0, response.Length - 1));
        return response;
    }

    private static byte Sum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        return (byte)sum;
    }

    // ==================== 辅助 ====================

    private DeviceCommandService Service(DeviceCommandOptions? options = null, TimeProvider? time = null)
        => new(_inspector, _bindings, _buses, _executor, options ?? DeviceCommandOptions.Default, time ?? TimeProvider.System, _log);

    private TopologyNode Node(string address) => _topology.Current!.GetNode(TopologyAddress.Parse(address)).Value;

    private async Task<DeviceDescriptor> AddModuleAsync(DriverId driver, string name, int address)
    {
        var added = await _devices.AddBatchAsync(new DeviceBatchRequest(_runtime.Id, driver, name, address, 1), CancellationToken.None);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        await WaitUntilAsync(() => _bindings.TryGet(added.Value[0].Id, out var b) && b.Status == DriverBindingStatus.Ready);
        return added.Value[0];
    }

    private async Task WireAsync(string address, ChannelRef channel, string? purpose = null)
    {
        var plan = _wiring.PlanAdd(Node(address).Id, channel, purpose, null);
        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
        Assert.True((await _wiring.ApplyAsync(plan.Value, CancellationToken.None)).IsSuccess);
    }

    private void SetOnline(params DeviceDescriptor[] devices)
    {
        foreach (var device in devices)
            _statuses.Set(new ModuleStatus(device.Id, _runtime.Id, ModuleCommState.Online, null, DateTimeOffset.UtcNow, 0, []));
    }

    private async Task StartPollingAsync(params DeviceDescriptor[] devices)
    {
        _polling = new DevicePollingService(_buses, _bindings, _executor, _statuses,
            new DevicePollingOptions { PollInterval = TimeSpan.FromMilliseconds(20) }, TimeProvider.System,
            NullLogger<DevicePollingService>.Instance);
        _polling.Start();
        foreach (var device in devices)
            await WaitUntilAsync(() => _statuses.Get(device.Id)?.State == ModuleCommState.Online);
    }

    /// <summary>
    /// 层位 C01-L01 接示例电源（供电），产品位 C01-L01-S01-F01-P01 接继电器第 3 路（切换 K1）
    /// </summary>
    private async Task<(DeviceDescriptor Power, DeviceDescriptor Relay)> WireDefaultAsync()
    {
        var power = await AddModuleAsync(SamplePower, "电源-01", 1);
        var relay = await AddModuleAsync(Relay, "继电器板", 2);
        await WireAsync("C01-L01", new ChannelRef(power.Id, SamplePowerDriver.OutputChannelKey, 1));
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(relay.Id, TestRelayDriver.Group, 3), purpose: "K1");
        return (power, relay);
    }

    private static DeviceCommandRequest PowerCommand(TopologyNode node, string key) => new(node.Id, ChannelKind.PowerSupply, null, key);

    private static DeviceCommandRequest RelayCommand(TopologyNode node, string key) => new(node.Id, ChannelKind.Switch, "k1", key);

    private int CommandFrames(byte function) => _transport.GetTxHistory().Count(f => f.Length >= 3 && f[0] == 0xAA && f[2] == function);

    private int RelayFrames(byte code) => _transport.GetTxHistory().Count(f => f.Length == 4 && f[0] == TestRelayDriver.RequestHeader && f[2] == code);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
            await Task.Delay(20);

        Assert.True(condition(), "等待超时");
    }

    // ==================== 回环：仿真下打开 / 关闭输出 ====================

    [Fact]
    public async Task Loopback_OutputOnThenOff_FromProduct_ReachesLayerPowerChannel()
    {
        var (power, relay) = await WireDefaultAsync();
        await StartPollingAsync(power, relay);
        var service = Service();
        var product = Node("C01-L01-S01-F01-P01");

        var on = service.Prepare(PowerCommand(product, SamplePowerDriver.OutputOnKey));
        Assert.True(on.IsSuccess, on.IsFailure ? on.Error.Message : null);

        // 仿真模式下输出动作不需要确认；电源接在层位上，作用于该层位下全部 4 个产品位
        Assert.False(on.Value.RequiresConfirmation);
        Assert.Equal(4, on.Value.AffectedProducts);
        Assert.Equal("层位 C01-L01 下全部 4 个产品位", on.Value.ScopeText);
        Assert.Equal(0, on.Value.Request.Arguments.Count);
        var described = on.Value.Describe();
        Assert.Contains("产品位 C01-L01-S01-F01-P01", described);
        Assert.Contains("来自层位 C01-L01", described);
        Assert.Contains("1# RS-485 · 电源-01", described);
        Assert.DoesNotContain(power.Id.ToString(), described);
        Assert.DoesNotContain(product.Id.ToString(), described);

        var executed = await service.ExecuteAsync(on.Value, confirmed: false, CancellationToken.None);
        Assert.True(executed.IsSuccess, executed.IsFailure ? executed.Error.Message : null);
        Assert.Contains(_transport.GetTxHistory(), f => f.SequenceEqual(new byte[] { 0xAA, 1, 0x10, 1, Sum([0xAA, 1, 0x10, 1]) }));

        var off = service.Prepare(PowerCommand(product, SamplePowerDriver.OutputOffKey));
        Assert.True((await service.ExecuteAsync(off.Value, confirmed: false, CancellationToken.None)).IsSuccess);
        Assert.Contains(_transport.GetTxHistory(), f => f.SequenceEqual(new byte[] { 0xAA, 1, 0x10, 0, Sum([0xAA, 1, 0x10, 0]) }));

        // 黑匣子：下发与完成各一条，含位置编码、模块、通道、结果与耗时
        var entries = _log.Entries.Select(e => e.Message).ToList();
        Assert.Contains(entries, m => m.StartsWith("设备命令下发 打开输出") && m.Contains("C01-L01-S01-F01-P01") && m.Contains("电源-01") && m.Contains("仿真"));
        Assert.Contains(entries, m => m.StartsWith("设备命令完成 关闭输出") && m.Contains("成功") && m.Contains(" ms"));
    }

    [Fact]
    public async Task Loopback_ChannelCommand_SendsWiredChannelNumber_AndSessionCallsNeverOverlapWithPolling()
    {
        var (power, relay) = await WireDefaultAsync();
        await StartPollingAsync(power, relay);
        var service = Service();
        var product = Node("C01-L01-S01-F01-P01");

        // 轮询每 20 ms 一轮，同时连续下发命令：会话的全部调用都须在通道锁内串行
        for (var i = 0; i < 20; i++)
        {
            var plan = service.Prepare(RelayCommand(product, i % 2 == 0 ? TestRelayDriver.On : TestRelayDriver.Off));
            Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
            Assert.Equal(1, plan.Value.AffectedProducts);
            var executed = await service.ExecuteAsync(plan.Value, confirmed: false, CancellationToken.None);
            Assert.True(executed.IsSuccess, executed.IsFailure ? executed.Error.Message : null);
        }

        Assert.Equal(10, RelayFrames(TestRelayDriver.OnCode));
        Assert.All(_transport.GetTxHistory().Where(f => f.Length == 4 && f[0] == TestRelayDriver.RequestHeader && f[2] == TestRelayDriver.OnCode),
            f => Assert.Equal(3, f[3]));
        Assert.Equal(0, _relay.Overlaps);
    }

    // ==================== 联锁 ====================

    [Fact]
    public async Task Interlock_ModuleNotOnline_IsRejectedWithReason_AndNothingSent()
    {
        await WireDefaultAsync();
        var service = Service();
        var before = _transport.GetTxHistory().Count;

        var plan = service.Prepare(PowerCommand(Node("C01-L01-S01-F01-P02"), SamplePowerDriver.OutputOnKey));

        Assert.Equal(DeviceCommandErrors.InterlockCode, plan.Error.Code);
        Assert.Contains("C01-L01", plan.Error.Message);
        Assert.Contains("等待", plan.Error.Message);
        Assert.Equal(before, _transport.GetTxHistory().Count);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("设备命令拒绝"));
    }

    [Fact]
    public async Task Interlock_AllEffectiveWiresOfTargetMustBeReady()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power);
        _statuses.Set(new ModuleStatus(relay.Id, _runtime.Id, ModuleCommState.CommFault, "响应超时", DateTimeOffset.UtcNow, 3, []));
        var service = Service();

        // P01 上的继电器通信异常：即使电源本身在线，从 P01 下发电源命令也被拒绝
        var fromP01 = service.Prepare(PowerCommand(Node("C01-L01-S01-F01-P01"), SamplePowerDriver.OutputOnKey));
        Assert.Equal(DeviceCommandErrors.InterlockCode, fromP01.Error.Code);
        Assert.Contains("响应超时", fromP01.Error.Message);

        // P02 只有层位的电源：可以下发
        Assert.True(service.Prepare(PowerCommand(Node("C01-L01-S01-F01-P02"), SamplePowerDriver.OutputOnKey)).IsSuccess);
    }

    [Fact]
    public async Task Interlock_OutputRejected_WhenAnyPositionUnderTheWireIsDisabled()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service();

        var disable = _topology.PlanNodeEdit(Node("C01-L01-S02-F01-P02").Id, new TopologyNodeEdit(null, false)).Value;
        Assert.True((await _topology.SaveAsync(disable, CancellationToken.None)).IsSuccess);
        await WaitUntilAsync(() => _wiring.Current.Topology?.GetNode(TopologyAddress.Parse("C01-L01-S02-F01-P02")).Value.IsEnabled == false);

        // 层位电源会同时作用到维护屏蔽中的 S02-F01-P02
        var plan = service.Prepare(PowerCommand(Node("C01-L01-S01-F01-P01"), SamplePowerDriver.OutputOnKey));
        Assert.Equal(DeviceCommandErrors.SubtreeDisabledCode, plan.Error.Code);
        Assert.Contains("C01-L01-S02-F01-P02", plan.Error.Message);

        // 产品位自己的继电器不经过停用的位置
        Assert.True(service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.On)).IsSuccess);

        // 选中的位置本身停用：联锁拒绝
        Assert.Equal(DeviceCommandErrors.InterlockCode,
            service.Prepare(PowerCommand(Node("C01-L01-S02-F01-P02"), SamplePowerDriver.OutputOnKey)).Error.Code);
    }

    /// <summary>
    /// 映射不做防呆后：一路通道共用到多个位置时，确认文案写明会同时动作的位置，共用的位置停用时输出类命令被拒绝；
    /// 同一路径上同功能同用途多于一路时，命令无法确定目标而拒绝
    /// </summary>
    [Fact]
    public async Task SharedChannel_IsDescribed_DisabledSharerBlocksOutput_AndAmbiguousTargetIsRejected()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service();
        var product = Node("C01-L01-S01-F01-P01");
        await WireAsync("C01-L02-S01-F01-P01", new ChannelRef(relay.Id, TestRelayDriver.Group, 3), purpose: "K1");

        var plan = service.Prepare(RelayCommand(product, TestRelayDriver.On));
        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
        Assert.Equal([Node("C01-L02-S01-F01-P01").Id], plan.Value.SharedWith.Select(n => n.Id));
        Assert.Contains("共用：这路通道还接在 产品位 C01-L02-S01-F01-P01，会同时动作", plan.Value.Describe());

        var disable = _topology.PlanNodeEdit(Node("C01-L02-S01-F01-P01").Id, new TopologyNodeEdit(null, false)).Value;
        Assert.True((await _topology.SaveAsync(disable, CancellationToken.None)).IsSuccess);
        await WaitUntilAsync(() => _wiring.Current.Topology?.GetNode(TopologyAddress.Parse("C01-L02-S01-F01-P01")).Value.IsEnabled == false);

        var blocked = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.On));
        Assert.Equal(DeviceCommandErrors.SubtreeDisabledCode, blocked.Error.Code);
        Assert.Contains("C01-L02-S01-F01-P01", blocked.Error.Message);

        // 层位上再接一路同为"切换 K1"：P01 上匹配到 2 路，拒绝
        await WireAsync("C01-L01", new ChannelRef(relay.Id, TestRelayDriver.Group, 4), purpose: "K1");
        var ambiguous = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.Off));
        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, ambiguous.Error.Code);
        Assert.Contains("2 路", ambiguous.Error.Message);
    }

    [Fact]
    public async Task Interlock_IsCheckedAgainAtExecution()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service();
        var plan = service.Prepare(PowerCommand(Node("C01-L01-S01-F01-P02"), SamplePowerDriver.OutputOnKey)).Value;

        _statuses.Set(new ModuleStatus(power.Id, _runtime.Id, ModuleCommState.CommFault, "响应超时", DateTimeOffset.UtcNow, 3, []));
        var executed = await service.ExecuteAsync(plan, confirmed: true, CancellationToken.None);

        Assert.Equal(DeviceCommandErrors.InterlockCode, executed.Error.Code);
        Assert.Equal(0, CommandFrames(0x10));
    }

    [Fact]
    public async Task Execution_WireRemovedAfterPreview_IsRejected()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service();
        var product = Node("C01-L01-S01-F01-P01");
        var plan = service.Prepare(RelayCommand(product, TestRelayDriver.On)).Value;

        var wire = Assert.Single(_wiring.Current.GetOwn(product.Id));
        Assert.True((await _wiring.RemoveAsync([wire.Id], CancellationToken.None)).IsSuccess);
        var executed = await service.ExecuteAsync(plan, confirmed: false, CancellationToken.None);

        Assert.Equal(DeviceCommandErrors.NotWiredCode, executed.Error.Code);
        Assert.Equal(0, RelayFrames(TestRelayDriver.OnCode));
    }

    // ==================== 安全等级与确认 ====================

    [Fact]
    public async Task Hazardous_RequiresConfirmation_EveryTime()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service();
        var product = Node("C01-L01-S01-F01-P01");

        for (var i = 0; i < 2; i++)
        {
            var plan = service.Prepare(RelayCommand(product, TestRelayDriver.Pulse)).Value;
            Assert.True(plan.RequiresConfirmation);
            Assert.Contains("高危", plan.Describe());

            var refused = await service.ExecuteAsync(plan, confirmed: false, CancellationToken.None);
            Assert.Equal(DeviceCommandErrors.ConfirmationRequiredCode, refused.Error.Code);
            Assert.Equal(i, RelayFrames(TestRelayDriver.PulseCode));

            Assert.True((await service.ExecuteAsync(plan, confirmed: true, CancellationToken.None)).IsSuccess);
            Assert.Equal(i + 1, RelayFrames(TestRelayDriver.PulseCode));
        }
    }

    [Fact]
    public async Task Hardware_OutputRequiresConfirmation_ParameterDoesNot()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service(new DeviceCommandOptions { IsHardware = true });
        var product = Node("C01-L01-S01-F01-P01");

        var output = service.Prepare(PowerCommand(product, SamplePowerDriver.OutputOnKey)).Value;
        Assert.True(output.RequiresConfirmation);
        Assert.Contains("模式：实机", output.Describe());
        Assert.Equal(DeviceCommandErrors.ConfirmationRequiredCode,
            (await service.ExecuteAsync(output, confirmed: false, CancellationToken.None)).Error.Code);
        Assert.Equal(0, CommandFrames(0x10));

        var parameter = service.Prepare(RelayCommand(product, TestRelayDriver.SetDelay) with
        {
            Arguments = new Dictionary<string, double> { ["delay-ms"] = 50 },
        }).Value;
        Assert.False(parameter.RequiresConfirmation);
        Assert.True((await service.ExecuteAsync(parameter, confirmed: false, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Confirmation_Expires()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var time = new ManualTime();
        var service = Service(new DeviceCommandOptions { ConfirmationLifetime = TimeSpan.FromSeconds(30) }, time);
        var plan = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.Pulse)).Value;

        time.Advance(TimeSpan.FromSeconds(31));
        var executed = await service.ExecuteAsync(plan, confirmed: true, CancellationToken.None);

        Assert.Equal(DeviceCommandErrors.ExpiredCode, executed.Error.Code);
        Assert.Equal(0, RelayFrames(TestRelayDriver.PulseCode));
    }

    // ==================== 解析 ====================

    [Fact]
    public async Task Resolve_RejectsUnwiredKind_UnknownCommand_ModuleWideCommand_AndBadArguments()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service();
        var product = Node("C01-L01-S01-F01-P01");

        Assert.Equal(DeviceCommandErrors.NotWiredCode, service.Prepare(new DeviceCommandRequest(product.Id, ChannelKind.Load, null, "x")).Error.Code);
        Assert.Equal(DeviceCommandErrors.NotWiredCode, service.Prepare(new DeviceCommandRequest(product.Id, ChannelKind.Switch, "K2", TestRelayDriver.On)).Error.Code);
        Assert.Equal(DeviceCommandErrors.UnknownCommandCode, service.Prepare(RelayCommand(product, "relay.unknown")).Error.Code);

        // 4 路继电器板的模块级命令会影响接在其他位置上的通道
        var moduleWide = service.Prepare(RelayCommand(product, TestRelayDriver.AllOff));
        Assert.Equal(DeviceCommandErrors.ModuleWideCode, moduleWide.Error.Code);
        Assert.Contains("4 路", moduleWide.Error.Message);

        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, service.Prepare(RelayCommand(product, "Bad Key")).Error.Code);
        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, service.Prepare(RelayCommand(product, TestRelayDriver.SetDelay) with
        {
            Arguments = new Dictionary<string, double> { ["delay-ms"] = double.NaN },
        }).Error.Code);
        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, service.Prepare(RelayCommand(product, TestRelayDriver.SetDelay) with
        {
            Arguments = null!,
        }).Error.Code);
    }

    [Fact]
    public async Task Preview_FreezesArguments()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var arguments = new Dictionary<string, double> { ["delay-ms"] = 50 };
        var plan = Service().Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.SetDelay) with { Arguments = arguments }).Value;

        arguments["delay-ms"] = 5000;

        Assert.Equal(50, plan.Request.Arguments["delay-ms"]);
    }

    [Fact]
    public async Task ListCommands_ChannelCommands_AndModuleCommandsOnlyForSingleChannelModules()
    {
        var (power, relay) = await WireDefaultAsync();
        var service = Service();

        var powerWire = Assert.Single(_wiring.Current.GetByDevice(power.Id));
        Assert.Equal([SamplePowerDriver.OutputOnKey, SamplePowerDriver.OutputOffKey], service.ListCommands(powerWire).Select(c => c.Key));

        var relayWire = Assert.Single(_wiring.Current.GetByDevice(relay.Id));
        var relayCommands = service.ListCommands(relayWire);
        Assert.Equal([TestRelayDriver.On, TestRelayDriver.Off, TestRelayDriver.Pulse, TestRelayDriver.SetDelay, TestRelayDriver.TimedOn],
            relayCommands.Select(c => c.Key));

        // 声明了必填参数的命令要先给出数值：接线面板据此暂不列出它
        Assert.Equal([TestRelayDriver.TimedOn], relayCommands.Where(c => c.NeedsInput).Select(c => c.Key));
        Assert.Equal(["seconds", "delay"], relayCommands.Single(c => c.Key == TestRelayDriver.TimedOn).Arguments.Select(a => a.Key));
    }

    [Fact]
    public async Task DeclaredArguments_AreChecked_Defaulted_AndShownInConfirmation()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service(new DeviceCommandOptions { IsHardware = true });
        var product = Node("C01-L01-S01-F01-P01");
        DeviceCommandRequest TimedOn(Dictionary<string, double> arguments) => RelayCommand(product, TestRelayDriver.TimedOn) with { Arguments = arguments };

        var missing = service.Prepare(TimedOn(new()));
        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, missing.Error.Code);
        Assert.Contains("缺少参数「时长」", missing.Error.Message);

        var unknown = service.Prepare(TimedOn(new() { ["seconds"] = 2, ["volts"] = 5 }));
        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, unknown.Error.Code);
        Assert.Contains("没有参数 volts", unknown.Error.Message);

        var outOfRange = service.Prepare(TimedOn(new() { ["seconds"] = 61 }));
        Assert.Equal(DeviceCommandErrors.InvalidRequestCode, outOfRange.Error.Code);
        Assert.Contains("时长 61 s 超出范围 [0.1, 60] s", outOfRange.Error.Message);

        // 可选参数补上默认值，交给驱动的是完整参数表；确认文案与日志都列出设定值
        var plan = service.Prepare(TimedOn(new() { ["seconds"] = 2 })).Value;
        Assert.Equal(new Dictionary<string, double> { ["seconds"] = 2, ["delay"] = 0 }, plan.Request.Arguments);
        Assert.Contains("参数：时长 2 s、延迟 0 ms", plan.Describe());
        Assert.Contains("参数 时长 2 s、延迟 0 ms", plan.ToString());

        Assert.True((await service.ExecuteAsync(plan, confirmed: true, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, RelayFrames(TestRelayDriver.TimedOnCode));
    }

    // ==================== 执行失败 ====================

    [Fact]
    public async Task Failure_NoResponse_IsTransient_AndNotRetried()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        _transport.Responder = request => request.Span[0] == TestRelayDriver.RequestHeader ? null : Respond(request);
        var service = Service();

        var plan = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.On)).Value;
        var executed = await service.ExecuteAsync(plan, confirmed: false, CancellationToken.None);

        Assert.Equal(DeviceCommandErrors.TransientCode, executed.Error.Code);
        Assert.Contains("响应超时", executed.Error.Message);
        Assert.Equal(1, RelayFrames(TestRelayDriver.OnCode));
        Assert.Equal(BusState.Connected, _runtime.State);
    }

    [Fact]
    public async Task Failure_DeviceReportsFault_IsDeviceFault()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        _relayFaults = true;
        var service = Service();

        var plan = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.On)).Value;
        var executed = await service.ExecuteAsync(plan, confirmed: false, CancellationToken.None);

        Assert.Equal(DeviceCommandErrors.DeviceFaultCode, executed.Error.Code);
        Assert.Contains("故障码 07", executed.Error.Message);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("设备命令失败"));
    }

    [Fact]
    public async Task Failure_DriverThrows_TripsTheBus()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        _relay.ThrowOnCommand = true;
        var service = Service();

        var plan = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.On)).Value;
        var executed = await service.ExecuteAsync(plan, confirmed: false, CancellationToken.None);

        Assert.Equal(DeviceCommandErrors.DriverFaultCode, executed.Error.Code);
        await WaitUntilAsync(() => _runtime.State == BusState.SafeStop);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task Failure_ChannelHeldTooLong_TimesOutWithinTheLimit()
    {
        var (power, relay) = await WireDefaultAsync();
        SetOnline(power, relay);
        var service = Service(new DeviceCommandOptions { Timeout = TimeSpan.FromMilliseconds(300) });
        var plan = service.Prepare(RelayCommand(Node("C01-L01-S01-F01-P01"), TestRelayDriver.On)).Value;

        // 另一个事务占住通道 2 s（总线的通道锁等待上限为 5 s）：命令应在自己的 300 ms 时限内放弃
        var release = new TaskCompletionSource();
        var holder = _runtime.ExecuteAsync<int>(async (_, tx) =>
        {
            await release.Task.WaitAsync(TimeSpan.FromSeconds(2), tx);
            return Result<int>.Success(0);
        }, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var executed = await service.ExecuteAsync(plan, confirmed: false, CancellationToken.None);
        watch.Stop();
        release.SetResult();
        await holder;

        Assert.Equal(DeviceCommandErrors.TimeoutCode, executed.Error.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"耗时 {watch.Elapsed}");
        Assert.Equal(0, RelayFrames(TestRelayDriver.OnCode));
    }

    // ==================== 界面入口：拓扑页接线面板 ====================

    [Fact]
    public async Task WiringPanel_RunsOutputOnOff_ConfirmsHazardous_AndShowsInterlockReason()
    {
        var (power, relay) = await WireDefaultAsync();
        await StartPollingAsync(power, relay);
        var panel = new WiringPanelViewModel();
        panel.Attach(_inspector, Service());
        panel.ShowNode(Node("C01-L01-S01-F01-P01"));
        try
        {
            // 上级（层位）的电源在前，产品位自己的继电器在后；写参数类命令不在界面上提供
            Assert.Equal(2, panel.Rows.Count);
            var powerRow = panel.Rows[0];
            var relayRow = panel.Rows[1];
            Assert.Equal(["打开输出", "关闭输出"], powerRow.Commands.Select(c => c.Name));
            Assert.Equal(["吸合", "释放", "高压脉冲"], relayRow.Commands.Select(c => c.Name));
            Assert.True(relayRow.Commands[2].IsHazardous);

            // 仿真下输出动作直接执行
            powerRow.Commands[0].Command.Execute(null);
            await WaitUntilAsync(() => panel.Message?.StartsWith("已执行「打开输出」") == true);
            Assert.Contains("层位 C01-L01 下全部 4 个产品位", panel.Message);
            Assert.Equal(1, CommandFrames(0x10));

            powerRow.Commands[1].Command.Execute(null);
            await WaitUntilAsync(() => panel.Message?.StartsWith("已执行「关闭输出」") == true);
            Assert.Equal(2, CommandFrames(0x10));

            // 高危：先显示确认，确认后才发出报文；取消则什么都不发
            relayRow.Commands[2].Command.Execute(null);
            await WaitUntilAsync(() => panel.IsCommandConfirmOpen);
            Assert.True(panel.IsPendingHazardous);
            Assert.Contains("高压脉冲", panel.CommandConfirmText);
            panel.CancelCommandCommand.Execute(null);
            Assert.False(panel.IsCommandConfirmOpen);
            Assert.Equal(0, RelayFrames(TestRelayDriver.PulseCode));

            relayRow.Commands[2].Command.Execute(null);
            await WaitUntilAsync(() => panel.IsCommandConfirmOpen);
            panel.ConfirmCommandCommand.Execute(null);
            await WaitUntilAsync(() => panel.Message?.StartsWith("已执行「高压脉冲」") == true);
            Assert.Equal(1, RelayFrames(TestRelayDriver.PulseCode));

            // 联锁不满足：拒绝并显示原因
            await _polling!.DisposeAsync();
            _polling = null;
            _statuses.Set(new ModuleStatus(relay.Id, _runtime.Id, ModuleCommState.CommFault, "响应超时", DateTimeOffset.UtcNow, 3, []));
            powerRow.Commands[0].Command.Execute(null);
            await WaitUntilAsync(() => panel.Message?.StartsWith("联锁未满足") == true);
            Assert.True(panel.IsError);
            Assert.Contains("响应超时", panel.Message);
            Assert.Equal(2, CommandFrames(0x10));
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ==================== 测试替身 ====================

    /// <summary>
    /// 只推进 <see cref="GetUtcNow"/> 的时钟；计时器与时间戳仍用系统时间
    /// </summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// 4 路继电器板，虚构协议：请求 BB 地址 功能码 通道号，响应 CC 地址 功能码 通道号；故障响应 CC 地址 EE 故障码。
    /// 记录会话方法是否被并发进入（内核须保证同一会话的调用串行）。
    /// </summary>
    private sealed class TestRelayDriver : IDeviceDriver
    {
        public const string Id = "test.relay-4";
        public const string Group = "relay";
        public const string On = "relay.on";
        public const string Off = "relay.off";
        public const string Pulse = "relay.pulse";
        public const string SetDelay = "relay.set-delay";
        public const string AllOff = "relay.all-off";
        public const string TimedOn = "relay.timed-on";
        public const byte TimedOnCode = 0x06;
        public const byte RequestHeader = 0xBB;
        public const byte ResponseHeader = 0xCC;
        public const byte OnCode = 0x01;
        public const byte PulseCode = 0x03;
        public const byte FaultCode = 0xEE;

        private int _inside;
        private int _overlaps;

        public DriverInfo Info { get; } = new(Id, "测试继电器板", DeviceCategory.General, "", 1, 32)
        {
            Commands =
            [
                new CommandInfo(On, "吸合", CommandSafety.Output) { Group = Group },
                new CommandInfo(Off, "释放", CommandSafety.Output) { Group = Group },
                new CommandInfo(Pulse, "高压脉冲", CommandSafety.Hazardous) { Group = Group },
                new CommandInfo(SetDelay, "设置吸合延时", CommandSafety.Parameter) { Group = Group },
                new CommandInfo(AllOff, "全部释放", CommandSafety.Output),
                new CommandInfo(TimedOn, "定时吸合", CommandSafety.Output)
                {
                    Group = Group,
                    Arguments =
                    [
                        new CommandArgumentInfo("seconds", "时长", "s", 0.1, 60),
                        new CommandArgumentInfo("delay", "延迟", "ms", 0, 1000) { DefaultValue = 0 },
                    ],
                },
            ],
            Channels = [new ChannelGroupInfo(Group, "继电器", ChannelFunction.Switch, 4)],
        };

        public volatile bool ThrowOnCommand;

        public int Overlaps => Volatile.Read(ref _overlaps);

        public IDeviceSession CreateSession(DeviceContext context) => new Session(this, (byte)context.Address);

        private Scope Enter()
        {
            if (Interlocked.Increment(ref _inside) > 1)
                Interlocked.Increment(ref _overlaps);

            // 拉长临界区，让并发进入更容易暴露
            Thread.SpinWait(2000);
            return new Scope(this);
        }

        private readonly struct Scope(TestRelayDriver driver) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref driver._inside);
        }

        private sealed class Session(TestRelayDriver driver, byte address) : IDeviceSession
        {
            public IExchange BeginPoll()
            {
                using var _ = driver.Enter();
                return new Exchange(driver, address, 0x00, 0);
            }

            public IExchange BeginCommand(DeviceCommand command)
            {
                using var _ = driver.Enter();
                if (driver.ThrowOnCommand)
                    throw new InvalidOperationException("驱动缺陷");

                byte code = command.Key switch
                {
                    On => OnCode,
                    Off => 0x02,
                    Pulse => PulseCode,
                    SetDelay => 0x05,
                    TimedOn => TimedOnCode,
                    _ => 0,
                };
                return code == 0 || command.Channel is < 1 or > 4
                    ? new Failed($"不支持命令 {command.Key}[{command.Channel}]")
                    : new Exchange(driver, address, code, (byte)command.Channel);
            }
        }

        private sealed class Failed(string reason) : IExchange
        {
            public ExchangeStep Start() => new ExchangeStep.Failed(reason, FailureKind.Configuration);

            public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
            {
                frameLength = 0;
                return FrameStatus.Invalid;
            }

            public ExchangeStep OnFrame(ReadOnlySpan<byte> frame) => Start();
        }

        private sealed class Exchange(TestRelayDriver driver, byte address, byte code, byte channel) : IExchange
        {
            public ExchangeStep Start()
            {
                using var _ = driver.Enter();
                return new ExchangeStep.Send(new byte[] { RequestHeader, address, code, channel });
            }

            public FrameStatus Match(ReadOnlySpan<byte> received, out int frameLength)
            {
                using var _ = driver.Enter();
                frameLength = 0;
                if (received[0] != ResponseHeader)
                    return FrameStatus.Invalid;
                if (received.Length < 4)
                    return FrameStatus.NeedMoreData;
                if (received[1] != address)
                    return FrameStatus.Invalid;

                frameLength = 4;
                return FrameStatus.Complete;
            }

            public ExchangeStep OnFrame(ReadOnlySpan<byte> frame)
            {
                using var _ = driver.Enter();
                return frame[2] == FaultCode
                    ? new ExchangeStep.Failed($"设备返回故障码 {frame[3]:X2}", FailureKind.Device)
                    : new ExchangeStep.Completed([]);
            }
        }
    }
}
