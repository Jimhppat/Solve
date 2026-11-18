using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses;

/// <summary>
/// 每个测试使用独立的临时目录与数据库文件，结束后删除；不触碰程序真实的 config 目录。
/// </summary>
public sealed class SqliteBusConfigurationRepositoryTests : IAsyncLifetime
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
    private SqliteDatabase _database = null!;
    private SqliteBusConfigurationRepository _repository = null!;

    private string DatabasePath => Path.Combine(_directory, "nested", "config", "solve.db");

    public async Task InitializeAsync()
    {
        _database = (await OpenDatabaseAsync()).Value;
        _repository = new SqliteBusConfigurationRepository(_database, NullLogger.Instance, _time);
    }

    public Task DisposeAsync()
    {
        // 连接池会保持文件句柄，删除临时文件前必须清空
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private Task<Result<SqliteDatabase>> OpenDatabaseAsync()
        => SqliteDatabase.OpenAsync(new SqliteStorageOptions(DatabasePath), NullLogger.Instance, CancellationToken.None);

    private async Task ExecuteRawAsync(string sql, object? parameters = null)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<T> ScalarRawAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private static SerialBusDescriptor Serial(string port = "COM3") => new(BusId.New(), $"RS485-{port}", port)
    {
        BaudRate = 115_200,
        DataBits = 7,
        Parity = SerialParity.Even,
        StopBits = SerialStopBits.Two,
        Timeouts = new BusTimeouts(TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400)),
        Reconnect = new ReconnectPolicy(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(20), 1.5, null),
    };

    private static TcpBusDescriptor Tcp(string host = "192.168.1.10") => new(BusId.New(), "PLC-01", host, 502);

    // ---------- 初始化 ----------

    [Fact]
    public async Task Open_CreatesDirectoryAndSchema_WithWal()
    {
        Assert.True(File.Exists(DatabasePath));
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, await ScalarRawAsync<long>("PRAGMA user_version;"));
        Assert.Equal("wal", await ScalarRawAsync<string>("PRAGMA journal_mode;"), ignoreCase: true);
        Assert.Equal(2L, await ScalarRawAsync<long>("PRAGMA synchronous;")); // 2 = FULL
    }

    [Fact]
    public async Task Open_Again_IsIdempotent_AndKeepsData()
    {
        var descriptor = Tcp();
        Assert.True((await _repository.SaveAsync(descriptor, CancellationToken.None)).IsSuccess);
        SqliteConnection.ClearAllPools();

        var reopened = await OpenDatabaseAsync();

        Assert.True(reopened.IsSuccess, reopened.ToString());
        var repository = new SqliteBusConfigurationRepository(reopened.Value, NullLogger.Instance);
        var snapshot = (await repository.LoadAllAsync(CancellationToken.None)).Value;
        Assert.Equal(descriptor, Assert.Single(snapshot.Descriptors));
    }

    [Fact]
    public async Task Open_NewerSchemaVersion_IsRefused()
    {
        await ExecuteRawAsync("PRAGMA user_version = 99;");
        SqliteConnection.ClearAllPools();

        var result = await OpenDatabaseAsync();

        Assert.Equal(BusErrors.StorageFailedCode, result.Error.Code);
        Assert.Contains("v99", result.Error.Message);
    }

    [Fact]
    public async Task EmptyDatabase_LoadsEmptySnapshot()
    {
        var snapshot = (await _repository.LoadAllAsync(CancellationToken.None)).Value;

        Assert.Empty(snapshot.Descriptors);
        Assert.Empty(snapshot.Issues);
    }

    // ---------- 往返 ----------

    [Fact]
    public async Task Serial_RoundTrip_IsLossless()
    {
        var descriptor = Serial();

        Assert.True((await _repository.SaveAsync(descriptor, CancellationToken.None)).IsSuccess);
        var snapshot = (await _repository.LoadAllAsync(CancellationToken.None)).Value;

        Assert.Equal(descriptor, Assert.Single(snapshot.Descriptors));
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("fe80::1")]
    [InlineData("plc-01.factory.local")]
    public async Task Tcp_RoundTrip_IsLossless(string host)
    {
        var descriptor = Tcp(host);

        Assert.True((await _repository.SaveAsync(descriptor, CancellationToken.None)).IsSuccess);
        var snapshot = (await _repository.LoadAllAsync(CancellationToken.None)).Value;

        Assert.Equal(descriptor, Assert.Single(snapshot.Descriptors));
    }

    [Fact]
    public async Task StoredValues_AreHumanReadable()
    {
        var descriptor = Serial();
        await _repository.SaveAsync(descriptor, CancellationToken.None);

        Assert.Equal("Even", await ScalarRawAsync<string>("SELECT parity FROM buses;"));
        Assert.Equal("Two", await ScalarRawAsync<string>("SELECT stop_bits FROM buses;"));
        Assert.Equal(2500L, await ScalarRawAsync<long>("SELECT connect_timeout_ms FROM buses;"));
        Assert.Null(await ScalarRawAsync<long?>("SELECT reconnect_max_attempts FROM buses;"));
        Assert.Equal(descriptor.Id.ToString(), await ScalarRawAsync<string>("SELECT id FROM buses;"));
    }

    // ---------- 更新 / 删除 ----------

    [Fact]
    public async Task Save_ExistingId_Updates_AndPreservesCreatedAt()
    {
        var original = Tcp();
        await _repository.SaveAsync(original, CancellationToken.None);
        var createdAt = await ScalarRawAsync<string>("SELECT created_at FROM buses;");

        _time.Now = _time.Now.AddHours(1);
        var updated = original with { Name = "PLC-01-改", Port = 503 };
        Assert.True((await _repository.SaveAsync(updated, CancellationToken.None)).IsSuccess);

        Assert.Equal(updated, Assert.Single((await _repository.LoadAllAsync(CancellationToken.None)).Value.Descriptors));
        Assert.Equal(createdAt, await ScalarRawAsync<string>("SELECT created_at FROM buses;"));
        Assert.NotEqual(createdAt, await ScalarRawAsync<string>("SELECT updated_at FROM buses;"));
    }

    [Fact]
    public async Task Save_SameIdDifferentKind_ReplacesLinkFields()
    {
        var serial = Serial();
        await _repository.SaveAsync(serial, CancellationToken.None);

        var tcp = new TcpBusDescriptor(serial.Id, "改为网关", "10.0.0.5", 502);
        Assert.True((await _repository.SaveAsync(tcp, CancellationToken.None)).IsSuccess);

        Assert.Equal(tcp, Assert.Single((await _repository.LoadAllAsync(CancellationToken.None)).Value.Descriptors));
        Assert.Null(await ScalarRawAsync<string?>("SELECT port_name FROM buses;"));
    }

    [Fact]
    public async Task Delete_RemovesRow_AndMissingIdReturnsNotFound()
    {
        var descriptor = Tcp();
        await _repository.SaveAsync(descriptor, CancellationToken.None);

        Assert.True((await _repository.DeleteAsync(descriptor.Id, CancellationToken.None)).IsSuccess);
        Assert.Empty((await _repository.LoadAllAsync(CancellationToken.None)).Value.Descriptors);
        Assert.Equal(BusErrors.StorageNotFoundCode, (await _repository.DeleteAsync(descriptor.Id, CancellationToken.None)).Error.Code);
    }

    // ---------- 拒绝写入 ----------

    [Fact]
    public async Task InvalidDescriptor_IsNotSaved()
    {
        var result = await _repository.SaveAsync(Tcp() with { Port = 0 }, CancellationToken.None);

        Assert.Equal(BusErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Equal(0L, await ScalarRawAsync<long>("SELECT count(*) FROM buses;"));
    }

    [Fact]
    public async Task SubMillisecondTimeout_IsRejected()
    {
        var descriptor = Tcp() with
        {
            Timeouts = BusTimeouts.Default with { Read = TimeSpan.FromMilliseconds(1.5) },
        };

        var result = await _repository.SaveAsync(descriptor, CancellationToken.None);

        Assert.Equal(BusErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Contains("Timeouts.Read", result.Error.Message);
    }

    [Theory]
    [InlineData("COM3")]
    [InlineData("com3")]
    [InlineData(" COM3 ")]
    public async Task SerialPortUsedByAnotherBus_IsConflict(string port)
    {
        var first = Serial("COM3");
        Assert.True((await _repository.SaveAsync(first, CancellationToken.None)).IsSuccess);

        var result = await _repository.SaveAsync(Serial(port), CancellationToken.None);

        Assert.Equal(BusErrors.StorageConflictCode, result.Error.Code);

        // 同一总线重复保存不是冲突
        Assert.True((await _repository.SaveAsync(first with { BaudRate = 9600 }, CancellationToken.None)).IsSuccess);
    }

    // ---------- 损坏数据 ----------

    [Fact]
    public async Task OutOfRangeRow_IsReported_OtherRowsStillLoad()
    {
        var good = Tcp();
        var bad = Serial();
        await _repository.SaveAsync(good, CancellationToken.None);
        await _repository.SaveAsync(bad, CancellationToken.None);

        // 模拟现场有人用数据库工具把波特率改成 0
        await ExecuteRawAsync("UPDATE buses SET baud_rate = 0 WHERE id = @Id;", new { Id = bad.Id.ToString() });

        var snapshot = (await _repository.LoadAllAsync(CancellationToken.None)).Value;

        Assert.Equal(good, Assert.Single(snapshot.Descriptors));
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(bad.Id.ToString(), issue.RowId);
        Assert.Equal("Bus.Descriptor.BaudRate", Assert.Single(issue.Errors).Code);
    }

    [Fact]
    public async Task UnparsableId_IsReportedAsCorruptRow()
    {
        await _repository.SaveAsync(Tcp(), CancellationToken.None);
        await ExecuteRawAsync("UPDATE buses SET id = 'XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX';");

        var snapshot = (await _repository.LoadAllAsync(CancellationToken.None)).Value;

        Assert.Empty(snapshot.Descriptors);
        Assert.Equal(BusErrors.StorageCorruptRowCode, Assert.Single(Assert.Single(snapshot.Issues).Errors).Code);
    }

    [Fact]
    public async Task Schema_RejectsStructurallyInconsistentRows()
    {
        // 表级 CHECK：TCP 行不得携带串口字段，即使绕过仓储直接写库
        var tcp = Tcp();
        await _repository.SaveAsync(tcp, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteRawAsync("UPDATE buses SET port_name = 'COM1' WHERE id = @Id;", new { Id = tcp.Id.ToString() }));

        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotExists_ReturnsSuccessNull()
    {
        var result = await _repository.GetByIdAsync(BusId.New(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetByIdAsync_WhenExists_ReturnsDescriptor()
    {
        var descriptor = Serial("COM7");
        await _repository.SaveAsync(descriptor, CancellationToken.None);

        var result = await _repository.GetByIdAsync(descriptor.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(descriptor.Id, result.Value.Id);
        Assert.Equal("COM7", Assert.IsType<SerialBusDescriptor>(result.Value).PortName);
    }
}
