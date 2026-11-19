using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Devices;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;

namespace Solve.Core.Tests.Devices;

/// <summary>
/// 每个测试使用独立的临时目录与数据库文件，结束后删除；不触碰程序真实的 config 目录。
/// </summary>
public sealed class SqliteDeviceRepositoryTests : IAsyncLifetime
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero));
    private SqliteDatabase _database = null!;
    private SqliteBusConfigurationRepository _buses = null!;
    private SqliteDeviceRepository _repository = null!;

    private SqliteStorageOptions StorageOptions => new(Path.Combine(_directory, "solve.db"));

    public async Task InitializeAsync()
    {
        _database = (await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None)).Value;
        _buses = new SqliteBusConfigurationRepository(_database, NullLogger.Instance, _time);
        _repository = new SqliteDeviceRepository(_database, NullLogger.Instance, _time);
    }

    public Task DisposeAsync()
    {
        // 只清本库的连接池，见 SqliteDatabase.ClearPool 的说明
        SqliteDatabase.ClearPool(StorageOptions);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<BusId> SeedBusAsync(string port = "COM3")
    {
        var bus = new SerialBusDescriptor(BusId.New(), $"RS485-{port}", port);
        Assert.True((await _buses.SaveAsync(bus, CancellationToken.None)).IsSuccess);
        return bus.Id;
    }

    private static DeviceDescriptor Device(BusId busId, int address, string name = "程控电源")
        => new(DeviceId.New(), busId, name, address);

    private async Task<IReadOnlyList<DeviceDescriptor>> ListAsync(BusId busId)
        => (await _repository.ListByBusAsync(busId, CancellationToken.None)).Value;

    private async Task<T> ScalarRawAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    // ---------- 新增与读取 ----------

    [Fact]
    public async Task Add_ThenList_RoundTrips_OrderedByAddress()
    {
        var busId = await SeedBusAsync();
        var second = Device(busId, 2, "温控表") with { Remark = "1 号柜" };
        var first = Device(busId, 1);

        Assert.True((await _repository.AddAsync(second, CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddAsync(first, CancellationToken.None)).IsSuccess);

        Assert.Equal([first, second], await ListAsync(busId));
        Assert.Equal(2, (await _repository.CountByBusAsync(busId, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task List_OnlyReturnsDevicesOfThatBus()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");
        var onA = Device(busA, 1);
        Assert.True((await _repository.AddAsync(onA, CancellationToken.None)).IsSuccess);

        Assert.Equal([onA], await ListAsync(busA));
        Assert.Empty(await ListAsync(busB));
    }

    [Fact]
    public async Task Add_BlankRemark_IsStoredAsNull()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1) with { Remark = "   " };

        Assert.True((await _repository.AddAsync(device, CancellationToken.None)).IsSuccess);

        Assert.Null(Assert.Single(await ListAsync(busId)).Remark);
    }

    [Fact]
    public async Task Add_DuplicateAddressOnSameBus_IsConflict()
    {
        var busId = await SeedBusAsync();
        Assert.True((await _repository.AddAsync(Device(busId, 5), CancellationToken.None)).IsSuccess);

        var result = await _repository.AddAsync(Device(busId, 5, "另一台"), CancellationToken.None);

        Assert.Equal(DeviceErrors.AddressConflictCode, result.Error.Code);
        Assert.Single(await ListAsync(busId));
    }

    [Fact]
    public async Task Add_SameAddressOnOtherBus_IsAllowed()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");

        Assert.True((await _repository.AddAsync(Device(busA, 1), CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddAsync(Device(busB, 1), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Add_UnknownBus_IsBusNotFound()
    {
        var result = await _repository.AddAsync(Device(BusId.New(), 1), CancellationToken.None);

        Assert.Equal(DeviceErrors.BusNotFoundCode, result.Error.Code);
        Assert.Equal(0L, await ScalarRawAsync<long>("SELECT count(*) FROM bus_devices;"));
    }

    [Fact]
    public async Task Add_Invalid_IsRejected_WithoutWriting()
    {
        var busId = await SeedBusAsync();

        var result = await _repository.AddAsync(Device(busId, 0), CancellationToken.None);

        Assert.Equal(DeviceErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Empty(await ListAsync(busId));
    }

    // ---------- 修改 ----------

    [Fact]
    public async Task Update_ChangesFields_KeepsCreatedAt()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1);
        Assert.True((await _repository.AddAsync(device, CancellationToken.None)).IsSuccess);

        _time.Now = _time.Now.AddHours(1);
        var changed = device with { Name = "2 号电源", Address = 7, Remark = "新备注" };
        Assert.True((await _repository.UpdateAsync(changed, CancellationToken.None)).IsSuccess);

        Assert.Equal([changed], await ListAsync(busId));
        var parameters = new { Id = device.Id.ToString() };
        Assert.Equal("2026-11-19T08:00:00.0000000Z",
            await ScalarRawAsync<string>("SELECT created_at FROM bus_devices WHERE id = @Id;", parameters));
        Assert.Equal("2026-11-19T09:00:00.0000000Z",
            await ScalarRawAsync<string>("SELECT updated_at FROM bus_devices WHERE id = @Id;", parameters));
    }

    [Fact]
    public async Task Update_ToTakenAddress_IsConflict_AndKeepsOriginal()
    {
        var busId = await SeedBusAsync();
        var first = Device(busId, 1);
        var second = Device(busId, 2);
        Assert.True((await _repository.AddAsync(first, CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddAsync(second, CancellationToken.None)).IsSuccess);

        var result = await _repository.UpdateAsync(second with { Address = 1 }, CancellationToken.None);

        Assert.Equal(DeviceErrors.AddressConflictCode, result.Error.Code);
        Assert.Equal([first, second], await ListAsync(busId));
    }

    [Fact]
    public async Task Update_WithOtherBusId_IsNotFound_AndDoesNotMoveDevice()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");
        var device = Device(busA, 1);
        Assert.True((await _repository.AddAsync(device, CancellationToken.None)).IsSuccess);

        var result = await _repository.UpdateAsync(device with { BusId = busB }, CancellationToken.None);

        Assert.Equal(DeviceErrors.StorageNotFoundCode, result.Error.Code);
        Assert.Equal([device], await ListAsync(busA));
        Assert.Empty(await ListAsync(busB));
    }

    [Fact]
    public async Task Update_Missing_IsNotFound()
    {
        var busId = await SeedBusAsync();

        var result = await _repository.UpdateAsync(Device(busId, 1), CancellationToken.None);

        Assert.Equal(DeviceErrors.StorageNotFoundCode, result.Error.Code);
    }

    // ---------- 删除 ----------

    [Fact]
    public async Task Delete_RemovesOnlyThatDevice()
    {
        var busId = await SeedBusAsync();
        var keep = Device(busId, 1);
        var remove = Device(busId, 2);
        Assert.True((await _repository.AddAsync(keep, CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddAsync(remove, CancellationToken.None)).IsSuccess);

        Assert.True((await _repository.DeleteAsync(remove.Id, CancellationToken.None)).IsSuccess);

        Assert.Equal([keep], await ListAsync(busId));
        Assert.Equal(DeviceErrors.StorageNotFoundCode, (await _repository.DeleteAsync(remove.Id, CancellationToken.None)).Error.Code);
    }

    // ---------- 与总线的关系 ----------

    [Fact]
    public async Task DeleteBus_CascadesItsDevices_AndLeavesOtherBusesAlone()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");
        Assert.True((await _repository.AddAsync(Device(busA, 1), CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddAsync(Device(busA, 2), CancellationToken.None)).IsSuccess);
        var onB = Device(busB, 1);
        Assert.True((await _repository.AddAsync(onB, CancellationToken.None)).IsSuccess);

        Assert.True((await _buses.DeleteAsync(busA, CancellationToken.None)).IsSuccess);

        Assert.Empty(await ListAsync(busA));
        Assert.Equal(0, (await _repository.CountByBusAsync(busA, CancellationToken.None)).Value);
        Assert.Equal([onB], await ListAsync(busB));
    }

    [Fact]
    public async Task SaveBus_Again_KeepsItsDevices()
    {
        // 守护 buses 表的保存方式：改成 INSERT OR REPLACE 会先删旧行并级联清空设备
        var bus = new SerialBusDescriptor(BusId.New(), "RS485-COM3", "COM3");
        Assert.True((await _buses.SaveAsync(bus, CancellationToken.None)).IsSuccess);
        var device = Device(bus.Id, 1);
        Assert.True((await _repository.AddAsync(device, CancellationToken.None)).IsSuccess);

        Assert.True((await _buses.SaveAsync(bus with { BaudRate = 115_200 }, CancellationToken.None)).IsSuccess);

        Assert.Equal([device], await ListAsync(bus.Id));
    }

    [Fact]
    public async Task List_SkipsCorruptRow_ButCountsIt()
    {
        var busId = await SeedBusAsync();
        var good = Device(busId, 1);
        Assert.True((await _repository.AddAsync(good, CancellationToken.None)).IsSuccess);

        // 手工改库写入越界地址：读取时按不可信输入校验，不能作为设备返回
        await using (var connection = await _database.OpenConnectionAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync(
                "INSERT INTO bus_devices (id, bus_id, address, name, remark, created_at, updated_at) VALUES (@Id, @BusId, 999, 'bad', NULL, 'x', 'x');",
                new { Id = Guid.NewGuid().ToString("D"), BusId = busId.Value.ToString("D") });
        }

        Assert.Equal([good], await ListAsync(busId));
        Assert.Equal(2, (await _repository.CountByBusAsync(busId, CancellationToken.None)).Value);
    }
}
