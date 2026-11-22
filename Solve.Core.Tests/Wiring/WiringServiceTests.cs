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
    /// 存储层：一路通道可以接到多个位置；同一位置重复接同一路通道被唯一索引拦下（并发时也不会接两次）
    /// </summary>
    [Fact]
    public async Task SameChannel_SharedAcrossPositions_ButNotTwiceAtOnePosition_InStorage()
    {
        var daq = await AddModuleAsync(Daq, "采集卡", 1);
        var channel = new ChannelRef(daq.Id, "input", 1);
        var wire = new Wire(WireId.New(), Node("C01-L01-S01-F01-P01").Id, channel, ChannelKind.Measure);
        Assert.True((await _wiringRepository.AddRangeAsync([wire], CancellationToken.None)).IsSuccess);

        var shared = wire with { Id = WireId.New(), Node = Node("C01-L01-S01-F01-P02").Id };
        Assert.True((await _wiringRepository.AddRangeAsync([shared], CancellationToken.None)).IsSuccess);

        var duplicate = wire with { Id = WireId.New() };
        Assert.Equal(WiringErrors.StorageConflictCode, (await _wiringRepository.AddRangeAsync([duplicate], CancellationToken.None)).Error.Code);
        Assert.Equal(2, (await _wiringRepository.LoadAllAsync(CancellationToken.None)).Value.Count);
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
            await Dapper.SqlMapper.ExecuteAsync(connection, "ALTER TABLE topology_nodes DROP COLUMN ports; ALTER TABLE bus_devices DROP COLUMN channels; DROP TABLE topology_wires; PRAGMA user_version = 5;");
        SqliteDatabase.ClearPool(StorageOptions);

        var reopened = await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None);

        Assert.True(reopened.IsSuccess);
        Assert.Empty((await new SqliteWiringRepository(reopened.Value, NullLogger.Instance).LoadAllAsync(CancellationToken.None)).Value);
        Assert.Single((await new SqliteDeviceRepository(reopened.Value, NullLogger.Instance).ListByBusAsync(_busId, CancellationToken.None)).Value);
    }

    // ---------- 模块实例的通道配置 ----------

    private async Task<DeviceDescriptor> ConfigureAsync(DeviceDescriptor device, ModuleChannelConfig channels)
    {
        var updated = device with { Channels = channels };
        var saved = await _devices.UpdateAsync(updated, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        return updated;
    }

    [Fact]
    public async Task DisabledChannels_CannotBeWired_AndBatchSkipsThem()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        await ConfigureAsync(relay, new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [1, 3])]));

        Assert.Equal(WiringErrors.ChannelNotDeclaredCode,
            _wiring.PlanAdd(Node("C01-L01-S01-F01-P01").Id, new ChannelRef(relay.Id, "relay", 2), null, null).Error.Code);

        // 一个槽位下 2 个产品位：跳过停用的第 2 路，依次接第 1、3 路
        var group = Assert.Single(Assert.Single(_wiring.ListModules()).Channels);
        var plan = _wiring.PlanBatch(new WiringBatchRequest(Node("C01-L01-S01").Id, TopologyLevel.Product,
            [new ChannelSource(relay.Id, group)], StartIndex: 1)).Value;
        Assert.Equal([1, 3], plan.Added.Select(w => w.Channel.Index));
    }

    [Fact]
    public async Task ModuleWithEveryChannelDisabled_IsNotOffered()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        await ConfigureAsync(relay, new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [])]));

        Assert.Empty(_wiring.ListModules());
    }

    [Fact]
    public async Task DisablingOrDroppingAWiredChannel_IsRejected()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        await WireAsync("C01-L01-S01-F01-P01", new ChannelRef(relay.Id, "relay", 2));

        var disable = await _devices.UpdateAsync(relay with { Channels = new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [1])]) },
            CancellationToken.None);

        Assert.Equal(WiringErrors.ChannelsInUseByWiresCode, disable.Error.Code);
        Assert.Contains("继电器 继电器 2", disable.Error.Message);

        // 别名可以随时改：接线按（组，通道号）定位，不受名称影响
        await ConfigureAsync(relay, new ModuleChannelConfig([new ChannelGroupSetting("relay", aliases: [new(2, "3# 压合")])]));
        Assert.Equal("继电器 3# 压合", _wiring.DescribeChannel(new ChannelRef(relay.Id, "relay", 2)));
    }

    [Fact]
    public async Task GenericModule_WithManualChannels_CanBeWired_ButIsNeverReady()
    {
        var generic = await AddModuleAsync(DriverCatalog.GenericModule, "气缸阀岛", 1);
        Assert.Empty(_wiring.ListModules());

        await ConfigureAsync(generic, new ModuleChannelConfig([new ChannelGroupSetting("g1", "电磁阀", ChannelKind.Switch, 8)]));
        await WireAsync("C01-L01-S01-F01", new ChannelRef(generic.Id, "g1", 4), purpose: "压合");

        var status = Assert.Single(_inspector.Inspect(Node("C01-L01-S01-F01").Id));
        Assert.Equal(("1# RS-485 · 气缸阀岛", "电磁阀 4", WireHealth.NoProtocol), (status.ModuleText, status.ChannelText, status.Health));
        Assert.True(_inspector.CheckReady(Node("C01-L01-S01-F01").Id).IsFailure);
    }

    [Fact]
    public async Task ChannelConfig_IsPersisted()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        var config = new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [1, 2], aliases: [new(2, "压合")])]);
        await ConfigureAsync(relay, config);

        var stored = Assert.Single((await _deviceRepository.ListByBusAsync(_busId, CancellationToken.None)).Value);

        Assert.Equal(config, stored.Channels);
    }

    // ---------- 硬件映射页 ----------

    [Fact]
    public async Task MappingPage_MapsChannelToSelectedPosition_AndLocatesIt()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        await ConfigureAsync(relay, new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [1, 2], aliases: [new(2, "压合")])]));

        var page = new Solve.UI.ViewModels.HardwareMappingViewModel();
        page.AttachTopology(_topology);
        page.AttachWiring(_inspector);

        // 只列出启用的通道：模块标题 + 2 路
        var channels = page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().ToList();
        Assert.Equal(["继电器 1", "压合"], channels.Select(c => c.NameText));
        Assert.IsType<Solve.UI.ViewModels.MappingModuleViewModel>(page.Hardware[0]);

        // 系统根不能接线
        Assert.Equal(TopologyLevel.System, page.SelectedItem!.Node.Level);
        Assert.False(channels[1].MapCommand.CanExecute(null));

        // 选中一个治具位，把"压合"关联上去
        page.SelectedItem = page.Roots[0].Children[0].Children[0].Children[0].Children[0];
        Assert.Equal("治具位 C01-L01-S01-F01", page.SelectedTitle);
        page.PurposeInput = "压合";
        await channels[1].MapCommand.ExecuteAsync();

        var wire = Assert.Single(_wiring.Current.GetOwn(Node("C01-L01-S01-F01").Id));
        Assert.Equal((new ChannelRef(relay.Id, "relay", 2), "压合"), (wire.Channel, wire.Purpose));
        var mapped = page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().Single(c => c.NameText == "压合");
        Assert.True(mapped.IsMapped);
        Assert.StartsWith("治具位 C01-L01-S01-F01", mapped.MappedText);

        // 只看未映射；定位回到映射的位置
        page.UnmappedOnly = true;
        Assert.Equal(["继电器 1"], page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().Select(c => c.NameText));
        page.UnmappedOnly = false;
        page.SelectedItem = page.Roots[0];
        page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().Single(c => c.NameText == "压合").LocateCommand.Execute(null);
        Assert.Equal(Node("C01-L01-S01-F01").Id, page.SelectedItem!.Node.Id);
        page.Dispose();
    }

    // ---------- 映射草稿 ----------

    [Fact]
    public async Task PlanAssign_ChecksEachRow_AndOnlyPlansWhenAllRowsAreValid()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        var daq = await AddModuleAsync(Daq, "采集卡", 2);
        await ConfigureAsync(relay, new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [1, 2, 3])]));
        await WireAsync("C01-L02-S01-F01-P01", new ChannelRef(daq.Id, "input", 8));

        // 跨模块、跳着选；第 3 行用了停用的通道，第 4 行在同一位置重复接已接的通道；第 5 行把已接的通道再接到别的位置（共用，允许）
        WiringDraftRow[] rows =
        [
            new(Node("C01-L01-S01-F01-P01").Id, new ChannelRef(relay.Id, "relay", 3)) { Purpose = "压合" },
            new(Node("C01-L01-S01-F01-P02").Id, new ChannelRef(daq.Id, "input", 5)),
            new(Node("C01-L01-S02-F01-P01").Id, new ChannelRef(relay.Id, "relay", 4)),
            new(Node("C01-L02-S01-F01-P01").Id, new ChannelRef(daq.Id, "input", 8)),
            new(Node("C01-L01-S02-F01-P02").Id, new ChannelRef(daq.Id, "input", 8)),
        ];

        var preview = _wiring.PlanAssign(rows).Value;

        Assert.Equal([true, true, false, false, true], preview.Rows.Select(r => r.IsValid));
        Assert.Contains("没有启用", preview.Rows[2].Problem);
        Assert.Contains("C01-L02-S01-F01-P01", preview.Rows[3].Problem);
        Assert.Null(preview.Plan);
        Assert.Equal(2, preview.ProblemCount);

        var fixedPreview = _wiring.PlanAssign([.. rows[..2], rows[4]]).Value;
        Assert.True((await _wiring.ApplyAsync(fixedPreview.Plan!, CancellationToken.None)).IsSuccess);
        Assert.Equal("压合", _wiring.Current.GetOwn(Node("C01-L01-S01-F01-P01").Id).Single().Purpose);
        Assert.Equal(4, _wiring.Current.Count);
        Assert.Equal(2, _wiring.Current.GetByChannel(new ChannelRef(daq.Id, "input", 8)).Count);
    }

    [Fact]
    public async Task MappingDraft_PicksPositionsAndChannels_AssemblesAndConfirms()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        var daq = await AddModuleAsync(Daq, "采集卡", 2);

        var page = new Solve.UI.ViewModels.HardwareMappingViewModel();
        page.AttachTopology(_topology);
        page.AttachWiring(_inspector);
        page.IsDraftMode = true;

        // ① 范围选层位 C01-L01，层级默认最深一层（产品位，4 个），全选
        page.SelectedItem = page.Roots[0].Children[0].Children[0];
        var draft = page.Draft;
        Assert.Equal("层位 C01-L01", draft.ScopeText);
        Assert.Equal(TopologyLevel.Product, draft.SelectedLevel!.Level);
        draft.SelectAllPositionsCommand.Execute(null);
        Assert.Equal(4, draft.SelectedPositionCount);

        // ② 取通道：继电器第 2、4 路 + 采集卡第 1 路（跨模块、跳着选），共 3 路
        var channels = page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().ToList();
        Assert.All(channels, c => Assert.True(c.ShowCheckBox));
        channels.Single(c => c.Channel == new ChannelRef(relay.Id, "relay", 2)).IsChecked = true;
        channels.Single(c => c.Channel == new ChannelRef(relay.Id, "relay", 4)).IsChecked = true;
        channels.Single(c => c.Channel == new ChannelRef(daq.Id, "input", 1)).IsChecked = true;
        Assert.Equal("已选 3 路通道", draft.ChannelCountText);

        // ③ 组装：4 个位置、3 路通道，配 3 对并提示多出 1 个位置
        await draft.AssembleCommand.ExecuteAsync();
        Assert.Equal(3, draft.Rows.Count);
        Assert.Contains("多出的 1 个位置", draft.Message);
        Assert.Equal(["C01-L01-S01-F01-P01", "C01-L01-S01-F01-P02", "C01-L01-S02-F01-P01"],
            draft.Rows.Select(r => r.Position.Address.ToString()));

        // 调整：第 1、2 行交换通道；第 3 行移除
        draft.Rows[0].MoveDownCommand.Execute(null);
        draft.Rows[2].RemoveCommand.Execute(null);
        Assert.Equal(new ChannelRef(relay.Id, "relay", 4), draft.Rows[0].Channel.Channel);
        await WaitUntilAsync(() => draft.ConfirmCommand.CanExecute(null));

        await draft.ConfirmCommand.ExecuteAsync();

        Assert.Empty(draft.Rows);
        Assert.Equal("已映射 2 路", draft.Message);
        Assert.Equal(new ChannelRef(relay.Id, "relay", 4), _wiring.Current.GetOwn(Node("C01-L01-S01-F01-P01").Id).Single().Channel);
        Assert.Equal(new ChannelRef(relay.Id, "relay", 2), _wiring.Current.GetOwn(Node("C01-L01-S01-F01-P02").Id).Single().Channel);
        page.Dispose();
    }

    [Fact]
    public async Task MappingDraft_PairsChannelsWithOpenPorts_SameKindFirst_ThenAnyKind()
    {
        // 层位 C01-L01 声明"每个产品位要采集 V × 2"；它下面 4 个产品位共 8 个接点
        var ports = new[] { new PortGroup(ChannelKind.Measure, "V", 2) };
        Assert.True((await _topology.SaveAsync(
            _topology.PlanNodeEdit(Node("C01-L01").Id, new TopologyNodeEdit(null, true, Ports: ports)).Value, CancellationToken.None)).IsSuccess);
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        var daq = await AddModuleAsync(Daq, "采集卡", 2);

        var page = new Solve.UI.ViewModels.HardwareMappingViewModel();
        page.AttachTopology(_topology);
        page.AttachWiring(_inspector);
        page.IsDraftMode = true;
        page.SelectedItem = page.Roots[0].Children[0].Children[0];
        var draft = page.Draft;
        draft.SelectAllPositionsCommand.Execute(null);

        // 继电器排在前面、3 路采集在后：采集先配同功能的接点，继电器不被丢掉，填进剩下的第一个空接点（不按功能拦截）
        var channels = page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().ToList();
        channels.Single(c => c.Channel == new ChannelRef(relay.Id, "relay", 1)).IsChecked = true;
        foreach (var index in new[] { 1, 2, 3 })
            channels.Single(c => c.Channel == new ChannelRef(daq.Id, "input", index)).IsChecked = true;

        await draft.AssembleCommand.ExecuteAsync();

        Assert.Equal(
            [("C01-L01-S01-F01-P01", "V1", "input", 1), ("C01-L01-S01-F01-P01", "V2", "input", 2),
             ("C01-L01-S01-F01-P02", "V1", "input", 3), ("C01-L01-S01-F01-P02", "V2", "relay", 1)],
            draft.Rows.Select(r => (r.Position.Address.ToString(), r.Purpose, r.Channel.Channel.Group, r.Channel.Channel.Index)));
        Assert.Contains("还有 4 个接点没有分配通道", draft.Message);
        Assert.DoesNotContain("未使用", draft.Message);

        await WaitUntilAsync(() => draft.ConfirmCommand.CanExecute(null));
        await draft.ConfirmCommand.ExecuteAsync();

        // 接点名对上即算接上，功能不同的照样算；接上的通道自动取消勾选
        Assert.All(_wiring.Current.GetPortCoverage(Node("C01-L01-S01-F01-P01").Id), p => Assert.True(p.IsConnected));
        var p02 = _wiring.Current.GetPortCoverage(Node("C01-L01-S01-F01-P02").Id);
        Assert.All(p02, p => Assert.True(p.IsConnected));
        Assert.Equal(ChannelKind.Switch, p02[1].Wire!.Value.Wire.Kind);
        Assert.DoesNotContain(page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>(), c => c.IsChecked);

        // "缺少采集"筛选：P01、P02 接完了，不再列出；其余 2 个产品位还缺
        draft.Filter = Solve.UI.ViewModels.PositionFilterOption.All.Single(o => o.Missing == ChannelKind.Measure);
        Assert.Equal(["C01-L01-S02-F01-P01", "C01-L01-S02-F01-P02"],
            draft.Positions.Select(p => p.Node.Address.ToString()));

        // 接线面板写明功能不同
        page.SelectedItem = page.Roots[0].Children[0].Children[0].Children[0].Children[0].Children[1];
        Assert.Equal("已接（功能为切换）", page.Wiring.PortRows[1].StateText);
        page.Dispose();
    }

    [Fact]
    public async Task MappingDraft_SharedChannel_IsAllowed_DuplicateAtSamePosition_BlocksConfirm()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        var channel = new ChannelRef(relay.Id, "relay", 1);
        await WireAsync("C01-L01", channel, purpose: "电源");
        await WireAsync("C01-L01-S01-F01-P01", channel);

        var page = new Solve.UI.ViewModels.HardwareMappingViewModel();
        page.AttachTopology(_topology);
        page.AttachWiring(_inspector);
        page.IsDraftMode = true;
        page.SelectedItem = page.Roots[0].Children[0].Children[0];
        var draft = page.Draft;

        // 已映射的通道照样能勾选；层位已有同功能同用途的接线，不再拦截
        var row = page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().Single(c => c.Channel == channel);
        Assert.True(row.ShowCheckBox);
        Assert.EndsWith("等 2 个位置", row.MappedText);
        row.IsChecked = true;
        page.PurposeInput = "电源";

        // P01 上已经接过这路通道：这一行标出重复，不能确认
        draft.Positions[0].IsChecked = true;
        await draft.AssembleCommand.ExecuteAsync();
        Assert.True(Assert.Single(draft.Rows).HasProblem);
        Assert.False(draft.ConfirmCommand.CanExecute(null));

        // 换到 P02：同一路通道共用到第三个位置
        draft.Positions[0].IsChecked = false;
        draft.Positions[1].IsChecked = true;
        await draft.AssembleCommand.ExecuteAsync();
        await WaitUntilAsync(() => draft.ConfirmCommand.CanExecute(null));
        await draft.ConfirmCommand.ExecuteAsync();

        Assert.Equal(3, _wiring.Current.GetByChannel(channel).Count);
        page.Dispose();
    }

    /// <summary>
    /// 硬件列表的"关联"：已映射的通道还能关联到别的位置，当前位置已接过时不能再关联
    /// </summary>
    [Fact]
    public async Task MapButton_AllowsSharedChannel_ButNotTwiceAtSamePosition()
    {
        var relay = await AddModuleAsync(Relay, "继电器", 1);
        var channel = new ChannelRef(relay.Id, "relay", 1);
        await WireAsync("C01-L01", channel);

        var page = new Solve.UI.ViewModels.HardwareMappingViewModel();
        page.AttachTopology(_topology);
        page.AttachWiring(_inspector);
        Solve.UI.ViewModels.MappingChannelViewModel Row()
            => page.Hardware.OfType<Solve.UI.ViewModels.MappingChannelViewModel>().Single(c => c.Channel == channel);

        page.SelectedItem = page.Roots[0].Children[0].Children[0];
        Assert.False(Row().MapCommand.CanExecute(null));

        page.SelectedItem = page.Roots[0].Children[0].Children[1];
        Assert.True(Row().MapCommand.CanExecute(null));
        await Row().MapCommand.ExecuteAsync();

        Assert.Equal(2, _wiring.Current.GetByChannel(channel).Count);
        Assert.False(Row().MapCommand.CanExecute(null));
        page.Dispose();
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

        // 继电器板没有协议：只登记接线、不算故障，但联锁照样拒绝（没有协议就没有可信的状态）
        Assert.Equal(WireHealth.NoProtocol, statuses[1].Health);
        Assert.Equal(WireHealth.Waiting, _inspector.Summarize(product));
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

    [Fact]
    public async Task CheckReady_RejectsProductWithMissingPorts_UntilAllAreWired()
    {
        // 层位上接 VBAT，层位声明"下面每个产品位要 VBAT + VCC"
        var vbat = await AddModuleAsync(SamplePower, "VBAT 电源", 1);
        await WireAsync("C01-L02", new ChannelRef(vbat.Id, "output", 1), "VBAT");
        _statuses.Set(new ModuleStatus(vbat.Id, _busId, ModuleCommState.Online, null, DateTimeOffset.UtcNow, 0, []));

        var ports = new[] { new PortGroup(ChannelKind.PowerSupply, "VBAT", 1), new PortGroup(ChannelKind.PowerSupply, "VCC", 1) };
        var edit = _topology.PlanNodeEdit(Node("C01-L02").Id, new TopologyNodeEdit(null, true, Ports: ports));
        Assert.True(edit.IsSuccess, edit.IsFailure ? edit.Error.Message : null);
        Assert.Contains("接点改为", edit.Value.Operation);
        Assert.True((await _topology.SaveAsync(edit.Value, CancellationToken.None)).IsSuccess);

        var product = Node("C01-L02-S01-F01-P01").Id;
        var refused = _inspector.CheckReady(product);
        Assert.Equal(WiringErrors.PortsMissingCode, refused.Error.Code);
        Assert.Contains("供电 VCC", refused.Error.Message);
        Assert.DoesNotContain("VBAT", refused.Error.Message);

        // 接点只在产品位上核对：层位本身照常可用
        Assert.True(_inspector.CheckReady(Node("C01-L02").Id).IsSuccess);

        var vcc = await AddModuleAsync(SamplePower, "VCC 电源", 2);
        await WireAsync("C01-L02-S01-F01-P01", new ChannelRef(vcc.Id, "output", 1), "vcc");
        _statuses.Set(new ModuleStatus(vcc.Id, _busId, ModuleCommState.Online, null, DateTimeOffset.UtcNow, 0, []));

        Assert.True(_inspector.CheckReady(product).IsSuccess);
        Assert.Equal(WiringErrors.PortsMissingCode, _inspector.CheckReady(Node("C01-L02-S01-F01-P02").Id).Error.Code);
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
