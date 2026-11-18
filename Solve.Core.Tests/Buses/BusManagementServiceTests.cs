using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.Buses.TestSupport;

namespace Solve.Core.Tests.Buses;

public sealed class BusManagementServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private SqliteBusConfigurationRepository _repository = null!;
    private BusManager _manager = null!;
    private BusManagementService _service = null!;

    private string DatabasePath => Path.Combine(_directory, "config", "solve.db");

    public async Task InitializeAsync()
    {
        var dbResult = await SqliteDatabase.OpenAsync(new SqliteStorageOptions(DatabasePath), NullLogger.Instance, CancellationToken.None);
        _database = dbResult.Value;
        _repository = new SqliteBusConfigurationRepository(_database, NullLogger.Instance);

        _manager = new BusManager(
            new SimulatedTransportFactory(),
            new TestInterlock(),
            NullLoggerFactory.Instance);

        _service = new BusManagementService(
            _manager,
            _repository,
            NullLogger<BusManagementService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task CreateBusAsync_PersistsToDatabase_AndRegistersInRuntime()
    {
        var descriptor = new SerialBusDescriptor(BusId.New(), "PLC-COM1", "COM1")
        {
            BaudRate = 115200,
        };

        var result = await _service.CreateBusAsync(descriptor, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(descriptor.Id, result.Value.Id);
        Assert.Single(_service.GetAllRuntimes());

        var dbDescriptor = (await _service.GetDescriptorAsync(descriptor.Id, CancellationToken.None)).Value;
        Assert.NotNull(dbDescriptor);
        Assert.Equal("PLC-COM1", dbDescriptor.Name);
        Assert.Equal("COM1", Assert.IsType<SerialBusDescriptor>(dbDescriptor).PortName);
    }

    [Fact]
    public async Task CreateBusAsync_WhenValidationFails_ReturnsFailure_AndDoesNotPersist()
    {
        var invalid = new SerialBusDescriptor(BusId.Empty, "", "");

        var result = await _service.CreateBusAsync(invalid, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(_service.GetAllRuntimes());
    }

    [Fact]
    public async Task CreateBusAsync_WhenResourceConflicts_ReturnsConflict()
    {
        var first = new SerialBusDescriptor(BusId.New(), "Bus1", "COM3");
        var second = new SerialBusDescriptor(BusId.New(), "Bus2", "COM3");

        Assert.True((await _service.CreateBusAsync(first, CancellationToken.None)).IsSuccess);
        var secondResult = await _service.CreateBusAsync(second, CancellationToken.None);

        Assert.True(secondResult.IsFailure);
        Assert.Equal(BusErrors.ResourceConflictCode, secondResult.Error.Code);
        Assert.Single(_service.GetAllRuntimes());
    }

    [Fact]
    public async Task UpdateBusAsync_SoftParameters_HotReloadsWithoutBreakingRuntime()
    {
        var original = new SerialBusDescriptor(BusId.New(), "PLC-Main", "COM1")
        {
            BaudRate = 9600,
            Timeouts = new BusTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
        };
        Assert.True((await _service.CreateBusAsync(original, CancellationToken.None)).IsSuccess);

        var updated = original with
        {
            Name = "PLC-Main-Renamed",
            Timeouts = new BusTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
        };

        var updateResult = await _service.UpdateBusAsync(updated, CancellationToken.None);

        Assert.True(updateResult.IsSuccess);
        var runtime = _service.GetRuntime(original.Id).Value;
        Assert.Equal("PLC-Main-Renamed", runtime.Descriptor.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), runtime.Descriptor.Timeouts.Read);

        var dbDescriptor = (await _service.GetDescriptorAsync(original.Id, CancellationToken.None)).Value;
        Assert.NotNull(dbDescriptor);
        Assert.Equal("PLC-Main-Renamed", dbDescriptor.Name);
    }

    [Fact]
    public async Task UpdateBusAsync_SamePortDifferentBaudRate_DoesNotConflictWithSelf()
    {
        var original = new SerialBusDescriptor(BusId.New(), "Sensor", "COM4")
        {
            BaudRate = 9600,
        };
        Assert.True((await _service.CreateBusAsync(original, CancellationToken.None)).IsSuccess);

        var updated = original with
        {
            BaudRate = 115200,
        };

        var updateResult = await _service.UpdateBusAsync(updated, CancellationToken.None);

        Assert.True(updateResult.IsSuccess);
        var runtime = _service.GetRuntime(original.Id).Value;
        Assert.Equal(115200, Assert.IsType<SerialBusDescriptor>(runtime.Descriptor).BaudRate);
    }

    [Fact]
    public async Task UpdateBusAsync_WhenPortConflictsWithAnotherBus_ReturnsFailure_AndDoesNotCorruptDatabase()
    {
        var busA = new SerialBusDescriptor(BusId.New(), "BusA", "COM1");
        var busB = new SerialBusDescriptor(BusId.New(), "BusB", "COM2");
        Assert.True((await _service.CreateBusAsync(busA, CancellationToken.None)).IsSuccess);
        Assert.True((await _service.CreateBusAsync(busB, CancellationToken.None)).IsSuccess);

        // 尝试把 BusB 改成 COM1（已被 BusA 占用）
        var conflictUpdate = busB with { PortName = "COM1" };
        var updateResult = await _service.UpdateBusAsync(conflictUpdate, CancellationToken.None);

        Assert.True(updateResult.IsFailure);
        Assert.Equal(BusErrors.ResourceConflictCode, updateResult.Error.Code);

        // 确认数据库里 BusB 依然是 COM2
        var dbBusB = (await _service.GetDescriptorAsync(busB.Id, CancellationToken.None)).Value;
        Assert.Equal("COM2", Assert.IsType<SerialBusDescriptor>(dbBusB).PortName);
    }

    [Fact]
    public async Task DeleteBusAsync_UnregistersRuntime_AndDeletesFromDatabase()
    {
        var bus = new TcpBusDescriptor(BusId.New(), "Scanner", "192.168.1.100", 502);
        Assert.True((await _service.CreateBusAsync(bus, CancellationToken.None)).IsSuccess);

        var deleteResult = await _service.DeleteBusAsync(bus.Id, CancellationToken.None);

        Assert.True(deleteResult.IsSuccess);
        Assert.Empty(_service.GetAllRuntimes());
        Assert.False(_service.GetRuntime(bus.Id).IsSuccess);

        var dbCheck = (await _service.GetDescriptorAsync(bus.Id, CancellationToken.None)).Value;
        Assert.Null(dbCheck);
    }

    [Fact]
    public async Task RegistryEvents_EmitsExpectedEventsOnCrud()
    {
        var bus = new SerialBusDescriptor(BusId.New(), "Tester", "COM8") { BaudRate = 9600 };

        // 1. Create -> BusRegistered
        await _service.CreateBusAsync(bus, CancellationToken.None);
        Assert.True(_manager.RegistryEvents.TryRead(out var evt1));
        var registered = Assert.IsType<BusRegistered>(evt1);
        Assert.Equal(bus.Id, registered.BusId);

        // 2. Update -> BusReconfigured
        var updated = bus with { BaudRate = 19200 };
        await _service.UpdateBusAsync(updated, CancellationToken.None);
        Assert.True(_manager.RegistryEvents.TryRead(out var evt2));
        var reconfigured = Assert.IsType<BusReconfigured>(evt2);
        Assert.Equal(bus.Id, reconfigured.BusId);

        // 3. Delete -> BusUnregistered
        await _service.DeleteBusAsync(bus.Id, CancellationToken.None);
        Assert.True(_manager.RegistryEvents.TryRead(out var evt3));
        var unregistered = Assert.IsType<BusUnregistered>(evt3);
        Assert.Equal(bus.Id, unregistered.BusId);
    }
}
