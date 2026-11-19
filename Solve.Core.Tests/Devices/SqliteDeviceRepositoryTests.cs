using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Devices;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

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

    private static readonly DriverId Generic = DriverId.Of("generic.module");

    private static DeviceDescriptor Device(BusId busId, int address, string name = "程控电源")
        => new(DeviceId.New(), busId, Generic, name, address);

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

        Assert.True((await _repository.AddRangeAsync([second], CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddRangeAsync([first], CancellationToken.None)).IsSuccess);

        Assert.Equal([first, second], await ListAsync(busId));
        Assert.Equal(2, (await _repository.CountByBusAsync(busId, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task List_OnlyReturnsDevicesOfThatBus()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");
        var onA = Device(busA, 1);
        Assert.True((await _repository.AddRangeAsync([onA], CancellationToken.None)).IsSuccess);

        Assert.Equal([onA], await ListAsync(busA));
        Assert.Empty(await ListAsync(busB));
    }

    [Fact]
    public async Task Add_BlankRemark_IsStoredAsNull()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1) with { Remark = "   " };

        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

        Assert.Null(Assert.Single(await ListAsync(busId)).Remark);
    }

    [Fact]
    public async Task Add_DuplicateAddressOnSameBus_IsConflict()
    {
        var busId = await SeedBusAsync();
        Assert.True((await _repository.AddRangeAsync([Device(busId, 5)], CancellationToken.None)).IsSuccess);

        var result = await _repository.AddRangeAsync([Device(busId, 5, "另一台")], CancellationToken.None);

        Assert.Equal(DeviceErrors.AddressConflictCode, result.Error.Code);
        Assert.Single(await ListAsync(busId));
    }

    [Fact]
    public async Task Add_SameAddressOnOtherBus_IsAllowed()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");

        Assert.True((await _repository.AddRangeAsync([Device(busA, 1)], CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddRangeAsync([Device(busB, 1)], CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Add_UnknownBus_IsBusNotFound()
    {
        var result = await _repository.AddRangeAsync([Device(BusId.New(), 1)], CancellationToken.None);

        Assert.Equal(DeviceErrors.BusNotFoundCode, result.Error.Code);
        Assert.Equal(0L, await ScalarRawAsync<long>("SELECT count(*) FROM bus_devices;"));
    }

    [Fact]
    public async Task Add_Invalid_IsRejected_WithoutWriting()
    {
        var busId = await SeedBusAsync();

        var result = await _repository.AddRangeAsync([Device(busId, 0)], CancellationToken.None);

        Assert.Equal(DeviceErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Empty(await ListAsync(busId));
    }

    [Fact]
    public async Task AddRange_WithOneConflict_WritesNothing()
    {
        var busId = await SeedBusAsync();
        var existing = Device(busId, 3);
        Assert.True((await _repository.AddRangeAsync([existing], CancellationToken.None)).IsSuccess);

        // 第三个与已有地址冲突：整批回滚，前两个也不能留下
        var result = await _repository.AddRangeAsync([Device(busId, 1), Device(busId, 2), Device(busId, 3)], CancellationToken.None);

        Assert.Equal(DeviceErrors.AddressConflictCode, result.Error.Code);
        Assert.Equal([existing], await ListAsync(busId));
    }

    [Fact]
    public async Task AddRange_StoresDriver()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1);

        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

        Assert.Equal(Generic, Assert.Single(await ListAsync(busId)).Driver);
        Assert.Equal("generic.module", await ScalarRawAsync<string>("SELECT driver_id FROM bus_devices;"));
    }

    // ---------- 修改 ----------

    [Fact]
    public async Task Update_ChangesFields_KeepsCreatedAt()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1);
        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

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
        Assert.True((await _repository.AddRangeAsync([first], CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddRangeAsync([second], CancellationToken.None)).IsSuccess);

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
        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

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
    public async Task DeleteRange_RemovesOnlyThoseDevices_AndCountsDeleted()
    {
        var busId = await SeedBusAsync();
        var keep = Device(busId, 1);
        var removeA = Device(busId, 2);
        var removeB = Device(busId, 3);
        Assert.True((await _repository.AddRangeAsync([keep, removeA, removeB], CancellationToken.None)).IsSuccess);

        var deleted = await _repository.DeleteRangeAsync([removeA.Id, removeB.Id], CancellationToken.None);

        Assert.Equal(2, deleted.Value);
        Assert.Equal([keep], await ListAsync(busId));

        // 已被删除的不算失败，只是不计入
        Assert.Equal(0, (await _repository.DeleteRangeAsync([removeA.Id], CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Update_CanChangeDriver()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1);
        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

        var changed = device with { Driver = DriverId.Of("test.power") };
        Assert.True((await _repository.UpdateAsync(changed, CancellationToken.None)).IsSuccess);

        Assert.Equal([changed], await ListAsync(busId));
    }

    // ---------- 与总线的关系 ----------

    [Fact]
    public async Task DeleteBus_CascadesItsDevices_AndLeavesOtherBusesAlone()
    {
        var busA = await SeedBusAsync("COM3");
        var busB = await SeedBusAsync("COM4");
        Assert.True((await _repository.AddRangeAsync([Device(busA, 1)], CancellationToken.None)).IsSuccess);
        Assert.True((await _repository.AddRangeAsync([Device(busA, 2)], CancellationToken.None)).IsSuccess);
        var onB = Device(busB, 1);
        Assert.True((await _repository.AddRangeAsync([onB], CancellationToken.None)).IsSuccess);

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
        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

        Assert.True((await _buses.SaveAsync(bus with { BaudRate = 115_200 }, CancellationToken.None)).IsSuccess);

        Assert.Equal([device], await ListAsync(bus.Id));
    }

    [Fact]
    public async Task List_SkipsCorruptRow_ButCountsIt()
    {
        var busId = await SeedBusAsync();
        var good = Device(busId, 1);
        Assert.True((await _repository.AddRangeAsync([good], CancellationToken.None)).IsSuccess);

        // 手工改库写入越界地址：读取时按不可信输入校验，不能作为设备返回
        await using (var connection = await _database.OpenConnectionAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync(
                "INSERT INTO bus_devices (id, bus_id, driver_id, address, name, remark, created_at, updated_at) VALUES (@Id, @BusId, 'generic.module', 999, 'bad', NULL, 'x', 'x');",
                new { Id = Guid.NewGuid().ToString("D"), BusId = busId.Value.ToString("D") });
        }

        Assert.Equal([good], await ListAsync(busId));
        Assert.Equal(2, (await _repository.CountByBusAsync(busId, CancellationToken.None)).Value);
    }

    // ---------- 模块参数 ----------

    private static ParameterValues Values(params (string Key, string Value)[] values)
        => new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    [Fact]
    public async Task Parameters_RoundTrip_AndUpdate()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1) with { Parameters = Values(("settle-ms", "500"), ("framing", "tcp")) };

        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);
        Assert.Equal([device], await ListAsync(busId));

        var changed = device with { Parameters = Values(("settle-ms", "800")) };
        Assert.True((await _repository.UpdateAsync(changed, CancellationToken.None)).IsSuccess);
        Assert.Equal([changed], await ListAsync(busId));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"settle-ms\": 500}")]
    [InlineData("{\"settle-ms\": null}")]
    [InlineData("{\"Bad Key\": \"1\"}")]
    public async Task CorruptParameters_RowIsSkipped(string parameters)
    {
        var busId = await SeedBusAsync();
        await using (var connection = await _database.OpenConnectionAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync(
                "INSERT INTO bus_devices (id, bus_id, driver_id, address, name, remark, parameters, created_at, updated_at) VALUES (@Id, @BusId, 'generic.module', 1, 'm', NULL, @Parameters, 'x', 'x');",
                new { Id = Guid.NewGuid().ToString("D"), BusId = busId.Value.ToString("D"), Parameters = parameters });
        }

        Assert.Empty(await ListAsync(busId));
    }

    [Fact]
    public async Task Migration_V4ToV5_AddsEmptyParameters_ToExistingModules()
    {
        var busId = await SeedBusAsync();
        var device = Device(busId, 1);
        Assert.True((await _repository.AddRangeAsync([device], CancellationToken.None)).IsSuccess);

        // 退回 v4：去掉 parameters 列（SQLite 3.35+ 支持 DROP COLUMN）
        await using (var connection = await _database.OpenConnectionAsync(CancellationToken.None))
            await connection.ExecuteAsync("ALTER TABLE bus_devices DROP COLUMN parameters; PRAGMA user_version = 4;");
        SqliteDatabase.ClearPool(StorageOptions);

        var reopened = await SqliteDatabase.OpenAsync(StorageOptions, NullLogger.Instance, CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.ToString());
        var repository = new SqliteDeviceRepository(reopened.Value, NullLogger.Instance);
        Assert.Equal([device], (await repository.ListByBusAsync(busId, CancellationToken.None)).Value);
    }
}
