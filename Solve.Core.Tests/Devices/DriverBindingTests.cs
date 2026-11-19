using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.DeviceLibrary;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Devices;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Tests.Buses.TestSupport;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.Devices;

/// <summary>
/// 模块 ↔ 驱动的绑定：参数规则、绑定器的各种结果、保存时的参数校验、跟随总线与模块变化的登记表
/// </summary>
public sealed class DriverBindingTests : IAsyncLifetime
{
    private sealed class FakeSession(DeviceContext context) : IDeviceSession
    {
        public DeviceContext Context { get; } = context;

        public IExchange BeginPoll() => throw new NotSupportedException("绑定测试不发起交互。");

        public IExchange BeginCommand(DeviceCommand command) => throw new NotSupportedException("绑定测试不发起交互。");
    }

    private sealed class FakeDriver(DriverInfo info, Func<DeviceContext, IDeviceSession?>? create = null) : IDeviceDriver
    {
        public DriverInfo Info { get; } = info;

        public IDeviceSession CreateSession(DeviceContext context) => create is null ? new FakeSession(context) : create(context)!;
    }

    private static readonly DriverInfo PowerInfo = new("test.power", "测试电源", DeviceCategory.PowerAndLoad, "", 1, 10)
    {
        Parameters =
        [
            new ParameterInfo("settle-ms", "稳定时间", ParameterKind.Integer, "200") { Minimum = 0, Maximum = 2000, Unit = "ms" },
            new ParameterInfo("framing", "报文封装", ParameterKind.Choice, "rtu")
            {
                Choices = [new ParameterChoice("rtu", "RTU"), new ParameterChoice("tcp", "TCP")],
            },
        ],
    };

    private static readonly DriverInfo BrokenInfo = new("test.broken", "坏驱动", DeviceCategory.General, "", 1, 255);

    private static readonly DriverId Power = DriverId.Of("test.power");
    private static readonly DriverId Broken = DriverId.Of("test.broken");

    private static DriverDescriptor Describe(DriverInfo info)
        => new(DriverId.Of(info.Id), info.Name, info.Category == DeviceCategory.PowerAndLoad ? HardwareCategory.PowerAndLoad : HardwareCategory.General,
            info.Description, info.MinAddress, info.MaxAddress);

    private static DriverCatalog CreateCatalog(Func<DeviceContext, IDeviceSession?>? brokenCreate = null)
        => new(
            [.. DriverCatalog.BuiltIns, Describe(PowerInfo), Describe(BrokenInfo)],
            new Dictionary<DriverId, IDeviceDriver>
            {
                [Power] = new FakeDriver(PowerInfo),
                [Broken] = new FakeDriver(BrokenInfo, brokenCreate ?? (_ => throw new InvalidOperationException("驱动内部错误"))),
            });

    private static DeviceDescriptor Module(DriverId driver, int address = 1, ParameterValues? parameters = null)
        => new(DeviceId.New(), BusId.New(), driver, "模块", address) { Parameters = parameters ?? ParameterValues.Empty };

    private static ParameterValues Values(params (string Key, string Value)[] values)
        => new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    // ---------- 参数规则 ----------

    [Fact]
    public void Normalize_DropsDefaults_AndKeepsChangedValues()
    {
        var normalized = DriverParameters.Normalize(PowerInfo, Values(("settle-ms", " 500 "), ("framing", "rtu")));

        Assert.Equal(Values(("settle-ms", "500")), normalized.Value);
    }

    [Fact]
    public void Normalize_RejectsUndeclaredAndInvalidValues()
    {
        var result = DriverParameters.Normalize(PowerInfo, Values(("settle-ms", "5000"), ("unknown", "1")));

        Assert.Equal(DeviceErrors.DescriptorCodePrefix + "Parameters", result.Error.Code);
        Assert.Contains("unknown", result.Error.Message);
        Assert.Contains("稳定时间", result.Error.Message);
    }

    [Fact]
    public void Resolve_FillsDefaults_AndIgnoresParametersNoLongerDeclared()
    {
        var resolved = DriverParameters.Resolve(PowerInfo, Values(("framing", "tcp"), ("removed", "x"))).Value;

        Assert.Equal(200, resolved.GetInteger("settle-ms"));
        Assert.Equal("tcp", resolved.GetText("framing"));
        Assert.False(resolved.Values.ContainsKey("removed"));
    }

    // ---------- 绑定器 ----------

    [Fact]
    public void Bind_LoadedDriver_IsReady_WithContext()
    {
        var binder = new DriverBinder(CreateCatalog(), NullLogger<DriverBinder>.Instance);

        var binding = binder.Bind(Module(Power, address: 3, Values(("settle-ms", "500"))), LinkKind.Tcp);

        Assert.Equal(DriverBindingStatus.Ready, binding.Status);
        var context = Assert.IsType<FakeSession>(binding.Session).Context;
        Assert.Equal((3, LinkKind.Tcp, 500L, "rtu"),
            (context.Address, context.Link, context.Parameters.GetInteger("settle-ms"), context.Parameters.GetText("framing")));
        Assert.Same(context, binding.Context);
    }

    [Fact]
    public void Bind_GenericModule_HasNoProtocol()
        => Assert.Equal(DriverBindingStatus.NoProtocol,
            new DriverBinder(CreateCatalog(), NullLogger<DriverBinder>.Instance).Bind(Module(DriverCatalog.GenericModule), LinkKind.Serial).Status);

    [Fact]
    public void Bind_UnknownDriver_IsNotLoaded()
        => Assert.Equal(DriverBindingStatus.DriverNotLoaded,
            new DriverBinder(CreateCatalog(), NullLogger<DriverBinder>.Instance).Bind(Module(DriverId.Of("gone.driver")), LinkKind.Serial).Status);

    [Fact]
    public void Bind_AddressOutsideDriverRange_IsRejected()
        => Assert.Equal(DriverBindingStatus.AddressOutOfRange,
            new DriverBinder(CreateCatalog(), NullLogger<DriverBinder>.Instance).Bind(Module(Power, address: 11), LinkKind.Serial).Status);

    [Fact]
    public void Bind_StoredParameterNoLongerValid_IsRejected()
    {
        var binding = new DriverBinder(CreateCatalog(), NullLogger<DriverBinder>.Instance)
            .Bind(Module(Power, parameters: Values(("settle-ms", "9999"))), LinkKind.Serial);

        Assert.Equal(DriverBindingStatus.InvalidParameters, binding.Status);
        Assert.Null(binding.Session);
    }

    [Fact]
    public void Bind_DriverThrowingOrReturningNull_FailsOnlyThatModule()
    {
        var throwing = new DriverBinder(CreateCatalog(), NullLogger<DriverBinder>.Instance).Bind(Module(Broken), LinkKind.Serial);
        var returningNull = new DriverBinder(CreateCatalog(_ => null), NullLogger<DriverBinder>.Instance).Bind(Module(Broken), LinkKind.Serial);

        Assert.Equal(DriverBindingStatus.SessionFailed, throwing.Status);
        Assert.Contains("驱动内部错误", throwing.Problem);
        Assert.Equal(DriverBindingStatus.SessionFailed, returningNull.Status);
    }

    // ---------- 登记表（真实总线管理与配置库） ----------

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private BusManager _manager = null!;
    private BusManagementService _buses = null!;
    private SqliteDeviceRepository _devices = null!;
    private DeviceBindingRegistry _registry = null!;
    private DeviceService _service = null!;

    private SqliteStorageOptions StorageOptions => new(Path.Combine(_directory, "solve.db"));

    public async Task InitializeAsync()
    {
        _database = (await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None)).Value;
        _manager = new BusManager(new SwitchableTransportFactory(), new TestInterlock(), NullLoggerFactory.Instance);
        _buses = new BusManagementService(_manager, new SqliteBusConfigurationRepository(_database, NullLogger.Instance),
            NullLogger<BusManagementService>.Instance);
        _devices = new SqliteDeviceRepository(_database, NullLogger.Instance);

        var catalog = CreateCatalog();
        _registry = new DeviceBindingRegistry(_devices, _buses, new DriverBinder(catalog, NullLogger<DriverBinder>.Instance),
            NullLogger<DeviceBindingRegistry>.Instance);
        _service = new DeviceService(_devices, catalog, _registry, new ModuleStatusStore(), NullLogger<DeviceService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _manager.DisposeAsync();
        SqliteDatabase.ClearPool(StorageOptions);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task<SerialBusDescriptor> CreateSerialBusAsync()
    {
        var bus = new SerialBusDescriptor(BusId.New(), "RS485", "COM7");
        Assert.True((await _buses.CreateBusAsync(bus, CancellationToken.None)).IsSuccess);
        return bus;
    }

    private Task<Result<IReadOnlyList<DeviceDescriptor>>> AddAsync(BusId busId, DriverId driver, int start, int count, ParameterValues? parameters = null)
        => _service.AddBatchAsync(new DeviceBatchRequest(busId, driver, "模块", start, count) { Parameters = parameters ?? ParameterValues.Empty },
            CancellationToken.None);

    /// <summary>
    /// 等待由总线事件驱动的后台重新绑定完成
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);

        Assert.True(condition(), "等待重新绑定超时");
    }

    [Fact]
    public async Task Start_BindsExistingModules_AndAddRebinds()
    {
        var bus = await CreateSerialBusAsync();
        Assert.True((await _devices.AddRangeAsync([Module(Power) with { BusId = bus.Id }], CancellationToken.None)).IsSuccess);

        await _registry.StartAsync(CancellationToken.None);
        Assert.Equal(DriverBindingStatus.Ready, Assert.Single(_registry.Get(bus.Id)).Status);

        Assert.True((await AddAsync(bus.Id, DriverCatalog.GenericModule, start: 5, count: 2)).IsSuccess);

        Assert.Equal(
            [DriverBindingStatus.Ready, DriverBindingStatus.NoProtocol, DriverBindingStatus.NoProtocol],
            _registry.Get(bus.Id).Select(b => b.Status));
    }

    [Fact]
    public async Task Add_StoresOnlyChangedParameters_AndRejectsInvalidOnes()
    {
        var bus = await CreateSerialBusAsync();
        await _registry.StartAsync(CancellationToken.None);

        var invalid = await AddAsync(bus.Id, Power, 1, 1, Values(("settle-ms", "abc")));
        var added = await AddAsync(bus.Id, Power, 1, 1, Values(("settle-ms", "500"), ("framing", "rtu")));

        Assert.Equal(DeviceErrors.DescriptorCodePrefix + "Parameters", invalid.Error.Code);
        var stored = Assert.Single((await _devices.ListByBusAsync(bus.Id, CancellationToken.None)).Value);
        Assert.Equal(Values(("settle-ms", "500")), stored.Parameters);
        Assert.Equal(500, Assert.Single(_registry.Get(bus.Id)).Context!.Parameters.GetInteger("settle-ms"));
        Assert.True(added.IsSuccess);
    }

    [Fact]
    public async Task Update_AndDelete_Rebind()
    {
        var bus = await CreateSerialBusAsync();
        await _registry.StartAsync(CancellationToken.None);
        var device = Assert.Single((await AddAsync(bus.Id, Power, 1, 1)).Value);

        Assert.True((await _service.UpdateAsync(device with { Address = 11 }, CancellationToken.None)).IsFailure);
        Assert.True((await _service.UpdateAsync(device with { Driver = DriverCatalog.GenericModule, Address = 100 }, CancellationToken.None)).IsSuccess);
        Assert.Equal(DriverBindingStatus.NoProtocol, Assert.Single(_registry.Get(bus.Id)).Status);

        var current = Assert.Single((await _devices.ListByBusAsync(bus.Id, CancellationToken.None)).Value);
        Assert.Equal(1, (await _service.DeleteAsync([current], CancellationToken.None)).Value);
        Assert.Empty(_registry.Get(bus.Id));
    }

    [Fact]
    public async Task BusReconfiguredToTcp_RebindsWithNewLink()
    {
        var bus = await CreateSerialBusAsync();
        await _registry.StartAsync(CancellationToken.None);
        Assert.True((await AddAsync(bus.Id, Power, 1, 1)).IsSuccess);
        Assert.Equal(LinkKind.Serial, Assert.Single(_registry.Get(bus.Id)).Context!.Link);

        var tcp = new TcpBusDescriptor(bus.Id, bus.Name, "127.0.0.1", 502);
        Assert.True((await _buses.UpdateBusAsync(tcp, CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => _registry.Get(bus.Id) is [{ Context.Link: LinkKind.Tcp }]);
    }

    [Fact]
    public async Task BusDeleted_RemovesItsBindings()
    {
        var bus = await CreateSerialBusAsync();
        await _registry.StartAsync(CancellationToken.None);
        Assert.True((await AddAsync(bus.Id, Power, 1, 1)).IsSuccess);

        Assert.True((await _buses.DeleteBusAsync(bus.Id, CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => _registry.Get(bus.Id).Count == 0);
    }
}
