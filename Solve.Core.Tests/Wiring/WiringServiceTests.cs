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
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Application.Topology;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Buses.TestSupport;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.Wiring;

/// <summary>
/// 接线服务 + SQLite 存储的整体行为：规则校验、写库、与拓扑 / 模块的联动删除、换驱动拦截、运行状态与联锁判据。
/// 每个测试使用独立的临时库，结束后删除；总线为仿真传输，不访问硬件。
/// </summary>
public sealed class WiringServiceTests : IAsyncLifetime
{
    private static readonly DriverId Daq = DriverId.Of("test.daq-8");
    private static readonly DriverId Relay = DriverId.Of("test.relay-4");
    private static readonly DriverId SamplePower = DriverId.Of("sample.power");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly SamplePowerDriver _sample = new();
    private SqliteDatabase _database = null!;
    private BusManager _manager = null!;
    private BusManagementService _buses = null!;
    private SqliteDeviceRepository _deviceRepository = null!;
    private DeviceBindingRegistry _bindings = null!;
    private DriverCatalog _catalog = null!;
    private TopologyService _topology = null!;
    private SqliteWiringRepository _wiringRepository = null!;
    private WiringService _wiring = null!;
    private ModuleStatusStore _statuses = null!;
    private DeviceService _devices = null!;
    private WiringInspector _inspector = null!;
    private BusId _busId;

    private SqliteStorageOptions StorageOptions => new(Path.Combine(_directory, "solve.db"));

    public async Task InitializeAsync()
    {
        _database = (await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None)).Value;
        _manager = new BusManager(new SimulatedTransportFactory(), new TestInterlock(), NullLoggerFactory.Instance);
        _buses = new BusManagementService(_manager, new SqliteBusConfigurationRepository(_database, NullLogger.Instance),
            NullLogger<BusManagementService>.Instance);

        // 采集卡与继电器板只有驱动说明（不含协议），足以验证接线；示例电源有实现，用于验证运行状态
        var info = _sample.Info;
        _catalog = new DriverCatalog(
            [
                .. DriverCatalog.BuiltIns,
                new DriverDescriptor(Daq, "8 路采集卡", HardwareCategory.General, "", 1, 32)
                {
                    Channels = [new ChannelGroup("input", "输入", ChannelKind.Measure, 8)],
                },
                new DriverDescriptor(Relay, "4 路继电器板", HardwareCategory.General, "", 1, 32)
                {
                    Channels = [new ChannelGroup("relay", "继电器", ChannelKind.Switch, 4)],
                },
                new DriverDescriptor(SamplePower, info.Name, HardwareCategory.PowerAndLoad, "", info.MinAddress, info.MaxAddress)
                {
                    Channels = [new ChannelGroup("output", "输出", ChannelKind.PowerSupply, 1)],
                },
            ],
            new Dictionary<DriverId, IDeviceDriver> { [SamplePower] = _sample });

        _deviceRepository = new SqliteDeviceRepository(_database, NullLogger.Instance);
        _bindings = new DeviceBindingRegistry(_deviceRepository, _buses,
            new DriverBinder(_catalog, NullLogger<DriverBinder>.Instance), NullLogger<DeviceBindingRegistry>.Instance);
        _topology = new TopologyService(new SqliteTopologyRepository(_database, NullLogger.Instance), NullLogger<TopologyService>.Instance);
        _wiringRepository = new SqliteWiringRepository(_database, NullLogger.Instance);
        _wiring = new WiringService(_wiringRepository, _topology, _bindings, _catalog, _buses, NullLogger<WiringService>.Instance);
        _statuses = new ModuleStatusStore();
        _devices = new DeviceService(_deviceRepository, _catalog, _bindings, _statuses, NullLogger<DeviceService>.Instance, _wiring);
        _inspector = new WiringInspector(_wiring, _bindings, _catalog, _statuses, _buses);

        // 1 柜 × 2 层 × 2 槽 × 1 治具 × 2 产品
        var plan = _topology.PlanLayout("老化房", new TopologyLayout(1, 2, 2, 1, 2)).Value;
        Assert.True((await _topology.SaveAsync(plan, CancellationToken.None)).IsSuccess);

        var bus = await _buses.CreateBusAsync(new SerialBusDescriptor(BusId.New(), "1# RS-485", "COM7"), CancellationToken.None);
        _busId = bus.Value.Id;
        await _bindings.StartAsync(CancellationToken.None);
        Assert.True((await _wiring.LoadAsync(CancellationToken.None)).IsSuccess);
        _wiring.Start();
    }

    public async Task DisposeAsync()
    {
        await _wiring.DisposeAsync();
        await _bindings.DisposeAsync();
        await _manager.DisposeAsync();
        SqliteDatabase.ClearPool(StorageOptions);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private TopologyNode Node(string address) => _topology.Current!.GetNode(TopologyAddress.Parse(address)).Value;

    private async Task<DeviceDescriptor> AddModuleAsync(DriverId driver, string name, int address)
    {
        var added = await _devices.AddBatchAsync(new DeviceBatchRequest(_busId, driver, name, address, 1), CancellationToken.None);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value[0];
    }

    private async Task WireAsync(string address, ChannelRef channel, string? purpose = null)
    {
        var plan = _wiring.PlanAdd(Node(address).Id, channel, purpose, null);
        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
        Assert.True((await _wiring.ApplyAsync(plan.Value, CancellationToken.None)).IsSuccess);
    }

    private async Task<int> StoredCountAsync() => (await _wiringRepository.LoadAllAsync(CancellationToken.None)).Value.Count;

    [Fact]
    public async Task Add_IsPersisted_AndReadBackAfterReload()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        await WireAsync("C01-L01-S01-F01-P02", new ChannelRef(daq.Id, "input", 5), purpose: "VBAT");

        var reloaded = new WiringService(_wiringRepository, _topology, _bindings, _catalog, _buses, NullLogger<WiringService>.Instance);
        Assert.True((await reloaded.LoadAsync(CancellationToken.None)).IsSuccess);

        var wire = Assert.Single(reloaded.Current.GetOwn(Node("C01-L01-S01-F01-P02").Id));
        Assert.Equal((new ChannelRef(daq.Id, "input", 5), ChannelKind.Measure, "VBAT"), (wire.Channel, wire.Kind, wire.Purpose));
        Assert.Equal("采集卡 输入 5", reloaded.DescribeChannel(wire.Channel));
        await reloaded.DisposeAsync();
    }

    [Fact]
    public async Task Add_UndeclaredChannel_OrUnknownModule_IsRejected()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        var node = Node("C01-L01-S01-F01-P01").Id;

        Assert.Equal(WiringErrors.ChannelNotDeclaredCode, _wiring.PlanAdd(node, new ChannelRef(daq.Id, "input", 9), null, null).Error.Code);
        Assert.Equal(WiringErrors.ChannelNotDeclaredCode, _wiring.PlanAdd(node, new ChannelRef(daq.Id, "relay", 1), null, null).Error.Code);
        Assert.Equal(WiringErrors.DeviceNotFoundCode, _wiring.PlanAdd(node, new ChannelRef(DeviceId.New(), "input", 1), null, null).Error.Code);
    }

    [Fact]
    public async Task GenericModule_HasNoChannels_AndIsNotOffered()
    {
        await AddModuleAsync(DriverCatalog.GenericModule, "通用", 1);
        var daq = await AddModuleAsync(Daq, "采集卡", 2);

        Assert.Equal([daq.Id], _wiring.ListModules().Select(m => m.Device.Id));
    }

    [Fact]
    public async Task Batch_WiresAllProducts_AcrossModules_InOneTransaction()
    {
        var first = await AddModuleAsync(Relay, "继电器-01", 1);
        await AddModuleAsync(Relay, "继电器-02", 2);
        var second = _wiring.ListModules().Single(m => m.Device.Name == "继电器-02").Device;
        var group = new ChannelGroup("relay", "继电器", ChannelKind.Switch, 4);

        var request = new WiringBatchRequest(_topology.Current!.Root.Children[0].Id, TopologyLevel.Product,
            [new ChannelSource(first.Id, group), new ChannelSource(second.Id, group)], StartIndex: 2);

        // 8 个产品位，从第 2 路起只有 3 + 4 = 7 路
        Assert.Equal(WiringErrors.NotEnoughChannelsCode, _wiring.PlanBatch(request).Error.Code);

        var plan = _wiring.PlanBatch(request with { StartIndex = 1 });
        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
        Assert.Equal(8, plan.Value.Added.Count);
        Assert.Equal(0, await StoredCountAsync());

        Assert.True((await _wiring.ApplyAsync(plan.Value, CancellationToken.None)).IsSuccess);
        Assert.Equal(8, await StoredCountAsync());

        // 第 5 个产品位（二层第一个）续接到第二块板的第 1 路
        Assert.Equal(new ChannelRef(second.Id, "relay", 1), _wiring.Current.GetOwn(Node("C01-L02-S01-F01-P01").Id)[0].Channel);
    }

    [Fact]
    public async Task StalePlan_IsRejected()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        var first = _wiring.PlanAdd(Node("C01-L01-S01-F01-P01").Id, new ChannelRef(daq.Id, "input", 1), null, null).Value;
        await WireAsync("C01-L01-S01-F01-P02", new ChannelRef(daq.Id, "input", 2));

        Assert.Equal(WiringErrors.StalePlanCode, (await _wiring.ApplyAsync(first, CancellationToken.None)).Error.Code);
    }

    /// <summary>
    /// 两个预览抢同一路通道：后保存的被唯一索引拦下，不会两处都接上
    /// </summary>
    [Fact]
    public async Task ConcurrentClaim_OfSameChannel_IsStoppedByStorage()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        var channel = new ChannelRef(daq.Id, "input", 1);
        var wire = new Wire(WireId.New(), Node("C01-L01-S01-F01-P01").Id, channel, ChannelKind.Measure);
        Assert.True((await _wiringRepository.AddRangeAsync([wire], CancellationToken.None)).IsSuccess);

        var duplicate = wire with { Id = WireId.New(), Node = Node("C01-L01-S01-F01-P02").Id };
        Assert.Equal(WiringErrors.StorageConflictCode, (await _wiringRepository.AddRangeAsync([duplicate], CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task RemovingPositions_RemovesTheirWires_InSameTransaction()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        await WireAsync("C01-L02-S01-F01-P01", new ChannelRef(daq.Id, "input", 1));
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(daq.Id, "input", 2));

        var plan = _topology.PlanRemove(Node("C01-L02").Id).Value;
        Assert.Equal(1, _wiring.Current.CountRemovedBy(plan.Candidate));
        Assert.True((await _topology.SaveAsync(plan, CancellationToken.None)).IsSuccess);

        Assert.Equal(1, await StoredCountAsync());
        Assert.Equal(1, _wiring.Current.Count);
    }

    /// <summary>
    /// 改编号、改名、扩容：位置 Id 不变，接线原样保留
    /// </summary>
    [Fact]
    public async Task RenumberingPositions_KeepsWires()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        await WireAsync("C01-L02-S01-F01-P01", new ChannelRef(daq.Id, "input", 1));

        var plan = _topology.PlanNodeEdit(Node("C01-L02").Id, new TopologyNodeEdit("二层", true, 5, null)).Value;
        Assert.True((await _topology.SaveAsync(plan, CancellationToken.None)).IsSuccess);

        Assert.Equal(1, await StoredCountAsync());
        Assert.Single(_wiring.Current.GetOwn(Node("C01-L05-S01-F01-P01").Id));
    }

    [Fact]
    public async Task DeletingModule_CascadesItsWires_AndServiceFollows()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(daq.Id, "input", 1));
        Assert.Equal(1, _devices.CountWires([daq.Id]));

        Assert.True((await _devices.DeleteAsync([daq], CancellationToken.None)).IsSuccess);

        Assert.Equal(0, await StoredCountAsync());
        await WaitUntilAsync(() => _wiring.Current.Count == 0);
    }

    [Fact]
    public async Task DeletingBus_CascadesModulesAndWires()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(daq.Id, "input", 1));

        Assert.True((await _buses.DeleteBusAsync(_busId, CancellationToken.None)).IsSuccess);

        Assert.Equal(0, await StoredCountAsync());
        await WaitUntilAsync(() => _wiring.Current.Count == 0);
    }

    [Fact]
    public async Task ChangingDriver_ThatDropsWiredChannels_IsRejected()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(daq.Id, "input", 6));

        var result = await _devices.UpdateAsync(daq with { Driver = Relay }, CancellationToken.None);

        Assert.Equal(WiringErrors.ChannelsInUseByWiresCode, result.Error.Code);
        Assert.Contains("采集卡 输入 6", result.Error.Message);
        Assert.True((await _devices.UpdateAsync(daq with { Name = "采集卡A" }, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Remove_And_Update_ArePersisted()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(relay.Id, "relay", 1));
        await WireAsync("C01-L01-S01-F01-P02", new ChannelRef(relay.Id, "relay", 2));
        var first = _wiring.Current.GetOwn(Node("C01-L01-S01-F01-P01").Id)[0];
        var second = _wiring.Current.GetOwn(Node("C01-L01-S01-F01-P02").Id)[0];

        Assert.True((await _wiring.UpdateAsync(second.Id, "压合", "W-002", CancellationToken.None)).IsSuccess);
        Assert.Equal(1, (await _wiring.RemoveAsync([first.Id], CancellationToken.None)).Value);

        var stored = Assert.Single((await _wiringRepository.LoadAllAsync(CancellationToken.None)).Value);
        Assert.Equal((second.Id, "压合", "W-002"), (stored.Id, stored.Purpose, stored.Label));
    }

    [Fact]
    public async Task Migration_V5ToV6_AddsWiresTable_AndKeepsModules()
    {
        await AddModuleAsync(Daq, "采集卡", 1);

        // 退回 v5：去掉接线表
        await using (var connection = await _database.OpenConnectionAsync(CancellationToken.None))
            await Dapper.SqlMapper.ExecuteAsync(connection, "DROP TABLE topology_wires; PRAGMA user_version = 5;");
        SqliteDatabase.ClearPool(StorageOptions);

        var reopened = await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None);

        Assert.True(reopened.IsSuccess);
        Assert.Empty((await new SqliteWiringRepository(reopened.Value, NullLogger.Instance).LoadAllAsync(CancellationToken.None)).Value);
        Assert.Single((await new SqliteDeviceRepository(reopened.Value, NullLogger.Instance).ListByBusAsync(_busId, CancellationToken.None)).Value);
    }

    // ---------- 运行状态与联锁判据 ----------

    [Fact]
    public async Task Inspect_ShowsInheritedFirst_AndHealthFollowsModuleState()
    {
        var power = await AddModuleAsync(SamplePower, "电源", 1);
        var relay = await AddModuleAsync(Relay, "继电器", 2);
        await WireAsync("C01-L01", new ChannelRef(power.Id, "output", 1));
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(relay.Id, "relay", 1));
        var product = Node("C01-L01-S01-F01-P01").Id;

        var statuses = _inspector.Inspect(product);
        Assert.Equal([true, false], statuses.Select(s => s.IsInherited));
        Assert.Equal("1# RS-485 · 电源", statuses[0].ModuleText);
        Assert.Equal(WireHealth.Waiting, statuses[0].Health);

        // 继电器板没有协议（驱动未就绪）：这路接线不可用，联锁拒绝
        Assert.Equal(WireHealth.Fault, statuses[1].Health);
        Assert.Equal(WireHealth.Fault, _inspector.Summarize(product));
        Assert.True(_inspector.CheckReady(product).IsFailure);

        // 电源在线：层位本身只有电源这一路，就绪
        _statuses.Set(new ModuleStatus(power.Id, _busId, ModuleCommState.Online, null, DateTimeOffset.UtcNow, 0,
            [new PointValue(SamplePowerDriver.VoltageKey, 12.5, DateTimeOffset.UtcNow)]));
        Assert.Equal(WireHealth.Ready, _inspector.Summarize(Node("C01-L01").Id));
        Assert.True(_inspector.CheckReady(Node("C01-L01").Id).IsSuccess);

        // 通信异常：故障安全方向，整层不可用
        _statuses.Set(new ModuleStatus(power.Id, _busId, ModuleCommState.CommFault, "超时", DateTimeOffset.UtcNow, 3, []));
        Assert.Equal(WireHealth.Fault, _inspector.Summarize(Node("C01-L01-S02").Id));
        Assert.True(_inspector.CheckReady(Node("C01-L01").Id).IsFailure);
    }

    [Fact]
    public async Task CheckReady_RejectsUnwiredOrDisabledPositions()
    {
        Assert.True(_inspector.CheckReady(Node("C01-L02").Id).IsFailure);
        Assert.Null(_inspector.Summarize(Node("C01-L02").Id));

        var power = await AddModuleAsync(SamplePower, "电源", 1);
        await WireAsync("C01-L02", new ChannelRef(power.Id, "output", 1));
        _statuses.Set(new ModuleStatus(power.Id, _busId, ModuleCommState.Online, null, DateTimeOffset.UtcNow, 0, []));
        Assert.True(_inspector.CheckReady(Node("C01-L02").Id).IsSuccess);

        var disable = _topology.PlanNodeEdit(Node("C01").Id, new TopologyNodeEdit(null, false, 1, null)).Value;
        Assert.True((await _topology.SaveAsync(disable, CancellationToken.None)).IsSuccess);
        Assert.Equal("Wiring.NodeDisabled", _inspector.CheckReady(Node("C01-L02").Id).Error.Code);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待超时");
            await Task.Delay(20);
        }
    }
}
