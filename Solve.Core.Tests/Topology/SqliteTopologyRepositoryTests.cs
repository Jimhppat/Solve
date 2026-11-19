using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Adapters.Persistence.Topology;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

/// <summary>
/// 每个测试使用独立的临时目录与数据库文件，结束后删除；不触碰程序真实的 config 目录。
/// </summary>
public sealed class SqliteTopologyRepositoryTests : IAsyncLifetime
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(T0);
    private SqliteDatabase _database = null!;
    private SqliteTopologyRepository _repository = null!;

    private string DatabasePath => Path.Combine(_directory, "config", "solve.db");

    public async Task InitializeAsync()
    {
        _database = (await OpenDatabaseAsync()).Value;
        _repository = new SqliteTopologyRepository(_database, NullLogger.Instance, _time);
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

    private async Task ExecuteRawAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(sql);
    }

    /// <summary>
    /// 在一个外键延迟检查的事务里执行，用来构造互相引用（成环）的坏数据
    /// </summary>
    private async Task ExecuteDeferredAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("PRAGMA defer_foreign_keys = ON;", transaction: transaction);
        await connection.ExecuteAsync(sql, transaction: transaction);
        await transaction.CommitAsync();
    }

    private async Task<T> ScalarRawAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private static EquipmentTopology Layout(int c, int l, int s, int f, int p, EquipmentTopology? current = null)
        => EquipmentTopology.Create(new TopologyLayout(c, l, s, f, p).ToDefinition("老化房", current).Value).Value;

    private async Task SaveAsync(EquipmentTopology topology)
    {
        var saved = await _repository.SaveAsync(topology, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.ToString());
    }

    private async Task<EquipmentTopology> LoadAsync()
    {
        var loaded = await _repository.LoadAsync(CancellationToken.None);
        Assert.True(loaded.IsSuccess, loaded.ToString());
        return Assert.IsType<EquipmentTopology>(loaded.Value);
    }

    private static IEnumerable<TopologyNode> AllNodes(EquipmentTopology topology)
        => TopologyLevels.All.SelectMany(topology.GetNodes);

    private static void AssertEquivalent(EquipmentTopology expected, EquipmentTopology actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.NodeCount, actual.NodeCount);
        foreach (var node in AllNodes(expected))
        {
            var twin = actual.GetNode(node.Id).Value;
            Assert.Equal(node.Address, twin.Address);
            Assert.Equal(node.Name, twin.Name);
            Assert.Equal(node.IsEnabled, twin.IsEnabled);
        }
    }

    private async Task<string> TimestampAsync(string column, string address)
        => await ScalarRawAsync<string>($"SELECT {column} FROM topology_nodes WHERE address = @Address;", new { Address = address });

    // ---------- 结构版本 ----------

    [Fact]
    public async Task Open_CreatesTopologyTable_AtLatestVersion()
    {
        Assert.Equal(3, SqliteDatabase.LatestSchemaVersion);
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, await ScalarRawAsync<long>("PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarRawAsync<long>("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'topology_nodes';"));
    }

    [Fact]
    public async Task Migration_V2ToV3_AddsTopologyTable()
    {
        await ExecuteRawAsync("DROP TABLE topology_nodes; PRAGMA user_version = 2;");
        SqliteConnection.ClearAllPools();

        var reopened = await OpenDatabaseAsync();

        Assert.True(reopened.IsSuccess, reopened.ToString());
        var repository = new SqliteTopologyRepository(reopened.Value, NullLogger.Instance);
        var topology = Layout(1, 1, 1, 1, 1);
        Assert.True((await repository.SaveAsync(topology, CancellationToken.None)).IsSuccess);
        AssertEquivalent(topology, (await repository.LoadAsync(CancellationToken.None)).Value!);
    }

    // ---------- 往返 ----------

    [Fact]
    public async Task Load_BeforeAnySave_ReturnsNull()
    {
        var loaded = await _repository.LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task RoundTrip_HeterogeneousTree_IsLossless()
    {
        TopologyNodeDefinition Node(int ordinal, params TopologyNodeDefinition[] children) => new(TopologyNodeId.New(), ordinal, children);
        var topology = EquipmentTopology.Create(new TopologyDefinition(TopologyNodeId.New(), "老化房 1#",
        [
            Node(1, Node(1, Node(1, Node(1, Node(1), Node(2)))) with { Name = "高温层" }) with { Name = "A 柜" },
            Node(3, Node(2, Node(5, Node(1, Node(4)) with { Enabled = false }))),
        ])).Value;

        await SaveAsync(topology);

        AssertEquivalent(topology, await LoadAsync());
    }

    [Fact]
    public async Task Rows_CarryBothIdentities_ReadableInDatabase()
    {
        var topology = Layout(1, 1, 1, 1, 2);
        await SaveAsync(topology);
        var product = topology.GetNode(TopologyAddress.Of(1, 1, 1, 1, 2)).Value;

        Assert.Equal("C01-L01-S01-F01-P02",
            await ScalarRawAsync<string>("SELECT address FROM topology_nodes WHERE id = @Id;", new { Id = product.Id.ToString() }));
        Assert.Equal(topology.Root.Id.ToString(),
            await ScalarRawAsync<string>("SELECT id FROM topology_nodes WHERE parent_id IS NULL AND address = 'SYS' AND ordinal = 0;"));
        Assert.Equal("老化房", await ScalarRawAsync<string>("SELECT name FROM topology_nodes WHERE parent_id IS NULL;"));
    }

    // ---------- 快照替换 ----------

    [Fact]
    public async Task Resave_SwappedOrdinals_KeepsIds()
    {
        // 两个柜体互换编号：中间状态会撞上同级序号唯一约束，快照替换必须能处理
        var original = Layout(2, 1, 1, 1, 1);
        await SaveAsync(original);
        var cabinet1 = original.GetNode(TopologyAddress.Of(1)).Value.Id;
        var cabinet2 = original.GetNode(TopologyAddress.Of(2)).Value.Id;

        var exported = original.ToDefinition();
        var swapped = exported with
        {
            Cabinets = [exported.Cabinets[0] with { Ordinal = 2 }, exported.Cabinets[1] with { Ordinal = 1 }],
        };
        await SaveAsync(EquipmentTopology.Create(swapped).Value);

        var loaded = await LoadAsync();
        Assert.Equal(TopologyAddress.Of(2), loaded.GetNode(cabinet1).Value.Address);
        Assert.Equal(TopologyAddress.Of(1), loaded.GetNode(cabinet2).Value.Address);
        Assert.Equal(TopologyAddress.Of(2, 1, 1, 1, 1),
            loaded.GetNode(original.GetNode(TopologyAddress.Of(1, 1, 1, 1, 1)).Value.Id).Value.Address);
    }

    [Fact]
    public async Task GrowAndShrink_ThroughStorage_KeepsIdsOfKeptPositions()
    {
        // 首次建档 → 落盘 → 读回 → 以读回的拓扑为基准扩容：Id 只来自库，不靠重新生成
        var first = Layout(1, 1, 1, 1, 2);
        await SaveAsync(first);

        var grown = Layout(2, 1, 1, 1, 3, current: await LoadAsync());
        await SaveAsync(grown);
        var afterGrow = await LoadAsync();
        foreach (var node in AllNodes(first))
            Assert.Equal(node.Address, afterGrow.GetNode(node.Id).Value.Address);
        Assert.Equal(afterGrow.NodeCount, await ScalarRawAsync<long>("SELECT count(*) FROM topology_nodes;"));

        var shrunk = Layout(1, 1, 1, 1, 1, current: afterGrow);
        await SaveAsync(shrunk);
        var afterShrink = await LoadAsync();
        Assert.Equal(6, afterShrink.NodeCount);
        Assert.Equal(6L, await ScalarRawAsync<long>("SELECT count(*) FROM topology_nodes;"));
        Assert.Equal(first.Root.Id, afterShrink.Root.Id);
        Assert.False(afterShrink.TryGetNode(afterGrow.GetNode(TopologyAddress.Of(2)).Value.Id, out _));
    }

    [Fact]
    public async Task Resave_KeepsCreatedAt_AndTouchesUpdatedAtOnlyForChangedRows()
    {
        await SaveAsync(Layout(2, 1, 1, 1, 1));
        var stamp0 = T0.UtcDateTime.ToString("O");

        _time.Now = T0.AddHours(1);
        var stamp1 = _time.Now.UtcDateTime.ToString("O");
        var exported = (await LoadAsync()).ToDefinition();
        var renamed = exported with { Cabinets = [exported.Cabinets[0] with { Name = "A 柜" }, .. exported.Cabinets.Skip(1)] };
        await SaveAsync(Layout(3, 1, 1, 1, 1, current: EquipmentTopology.Create(renamed).Value));

        Assert.Equal(stamp0, await TimestampAsync("created_at", "C01"));
        Assert.Equal(stamp1, await TimestampAsync("updated_at", "C01"));
        Assert.Equal(stamp0, await TimestampAsync("updated_at", "C02"));
        Assert.Equal(stamp1, await TimestampAsync("created_at", "C03"));
    }

    [Fact]
    public async Task FailedSave_RollsBack_PreviousSnapshotIsIntact()
    {
        var original = Layout(2, 1, 1, 1, 1);
        await SaveAsync(original);

        // 写到一半时触发器中止：已执行的清空与写入必须整体回滚
        await ExecuteRawAsync("""
            CREATE TRIGGER fail_on_c03 BEFORE INSERT ON topology_nodes
            WHEN NEW.address = 'C03'
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var result = await _repository.SaveAsync(Layout(3, 1, 1, 1, 1, current: original), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(TopologyErrors.StorageFailedCode, result.Error.Code);
        AssertEquivalent(original, await LoadAsync());
    }

    // ---------- 损坏数据：整体拒绝，不抛异常 ----------

    [Theory]
    [InlineData("UPDATE topology_nodes SET address = 'C09' WHERE address = 'C02';", "address 存为 'C09'")]
    [InlineData("UPDATE topology_nodes SET level = 3 WHERE address = 'C02';", "level 存为 3")]
    [InlineData("UPDATE topology_nodes SET ordinal = 1000 WHERE address = 'C01-L01-S01-F01-P01';", "序号必须在")]
    [InlineData("UPDATE topology_nodes SET enabled = 0 WHERE parent_id IS NULL;", "系统根不可停用")]
    [InlineData("UPDATE topology_nodes SET id = 'zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz' WHERE address = 'C01-L01-S01-F01-P01';", "不是有效的 UUID")]
    [InlineData("UPDATE topology_nodes SET name = '   ' WHERE parent_id IS NULL;", "系统名称不能为空")]
    public async Task TamperedRow_IsRejectedAsCorrupt(string tamper, string expectedDetail)
    {
        await SaveAsync(Layout(2, 1, 1, 1, 1));
        await ExecuteRawAsync(tamper);

        var loaded = await _repository.LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsFailure);
        Assert.Equal(TopologyErrors.StorageCorruptCode, loaded.Error.Code);
        Assert.Contains(expectedDetail, loaded.Error.Message);
    }

    [Fact]
    public async Task CycleDisconnectedFromRoot_IsRejectedAsCorrupt()
    {
        await SaveAsync(Layout(1, 1, 1, 1, 1));
        await ExecuteDeferredAsync("""
            INSERT INTO topology_nodes VALUES
                ('aaaaaaaa-0000-7000-8000-000000000001', 'aaaaaaaa-0000-7000-8000-000000000002', 2, 50, 'C50', NULL, 1, 't', 't'),
                ('aaaaaaaa-0000-7000-8000-000000000002', 'aaaaaaaa-0000-7000-8000-000000000001', 3, 1, 'C50-L01', NULL, 1, 't', 't');
            """);

        var loaded = await _repository.LoadAsync(CancellationToken.None);

        Assert.Equal(TopologyErrors.StorageCorruptCode, loaded.Error.Code);
        Assert.Contains("2 行无法从系统根到达", loaded.Error.Message);
    }

    [Fact]
    public async Task ChildUnderProduct_IsRejectedAsCorrupt()
    {
        await SaveAsync(Layout(1, 1, 1, 1, 1));
        await ExecuteRawAsync("""
            INSERT INTO topology_nodes
            SELECT 'bbbbbbbb-0000-7000-8000-000000000001', id, 6, 1, 'DEEP', NULL, 1, 't', 't'
              FROM topology_nodes WHERE address = 'C01-L01-S01-F01-P01';
            """);

        var loaded = await _repository.LoadAsync(CancellationToken.None);

        Assert.Equal(TopologyErrors.StorageCorruptCode, loaded.Error.Code);
        Assert.Contains("产品位", loaded.Error.Message);
    }

    [Fact]
    public async Task DatabaseConstraints_RejectSecondRoot()
    {
        await SaveAsync(Layout(1, 1, 1, 1, 1));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => ExecuteRawAsync(
            "INSERT INTO topology_nodes VALUES ('cccccccc-0000-7000-8000-000000000001', NULL, 1, 0, 'SYS2', 'X', 1, 't', 't');"));

        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }
}
