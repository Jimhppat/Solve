using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Persistence.Buses;
using Solve.Adapters.Persistence.Settings;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Adapters.Persistence.Topology;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Composition;
using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Composition;

/// <summary>
/// 组合根测试。每个测试使用独立临时目录；只验证"注册不连接"，不对任何真实设备做 I/O。
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class CompositionRootTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly List<AppHost> _hosts = [];

    private string DatabasePath => Path.Combine(_directory, "config", "solve.db");

    private string LogDirectory => Path.Combine(_directory, "logs");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private AppHost CreateHost(bool hardwareRequested = false, string? databasePath = null)
    {
        var host = AppHost.Create(new DesktopOptions(hardwareRequested, databasePath ?? DatabasePath, LogDirectory));
        _hosts.Add(host);
        return host;
    }

    private async Task<SqliteDatabase> OpenSeedDatabaseAsync()
        => (await SqliteDatabase.OpenAsync(new SqliteStorageOptions(DatabasePath), NullLogger.Instance, CancellationToken.None)).Value;

    private async Task SeedAsync(string? hardwareEnabled, params BusDescriptor[] descriptors)
    {
        var database = await OpenSeedDatabaseAsync();
        var repository = new SqliteBusConfigurationRepository(database, NullLogger.Instance);
        foreach (var descriptor in descriptors)
            Assert.True((await repository.SaveAsync(descriptor, CancellationToken.None)).IsSuccess);

        if (hardwareEnabled is not null)
        {
            var settings = new SqliteAppSettingsStore(database, NullLogger.Instance);
            Assert.True((await settings.SetAsync(TransportModePolicy.HardwareEnabledKey, hardwareEnabled, CancellationToken.None)).IsSuccess);
        }

        SqliteConnection.ClearAllPools();
    }

    // 回环地址 + 未使用端口：即使误连也不会触达现场设备
    private static TcpBusDescriptor Tcp(string name = "PLC-01", int port = 50_502) => new(BusId.New(), name, "127.0.0.1", port);

    // ==================== TransportModePolicy ====================

    public static TheoryData<bool, bool, string?, TransportMode> ModeTable => new()
    {
        // 参数, 读取成功, 存储值, 期望
        { false, true, "true", TransportMode.Simulation },
        { false, true, null, TransportMode.Simulation },
        { true, true, null, TransportMode.Simulation },
        { true, true, "false", TransportMode.Simulation },
        { true, true, "TRUE", TransportMode.Simulation },
        { true, true, "1", TransportMode.Simulation },
        { true, true, " true", TransportMode.Simulation },
        { true, false, null, TransportMode.Simulation },
        { true, true, "true", TransportMode.Hardware },
    };

    [Theory]
    [MemberData(nameof(ModeTable))]
    public void TransportModePolicy_RequiresBothConditions(bool requested, bool readOk, string? stored, TransportMode expected)
    {
        var setting = readOk
            ? Result<string?>.Success(stored)
            : Result<string?>.Failure(new Error("Settings.Storage.Failed", "磁盘错误"));

        var decision = TransportModePolicy.Decide(requested, setting);

        Assert.Equal(expected, decision.Mode);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    // ==================== DesktopOptions ====================

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--hardware" }, true)]
    [InlineData(new[] { "--verbose", "--hardware" }, true)]
    [InlineData(new[] { "--Hardware" }, false)]
    [InlineData(new[] { "--hardware=true" }, false)]
    [InlineData(new[] { "-hardware" }, false)]
    public void DesktopOptions_HardwareFlag_IsExactMatch(string[] args, bool expected)
    {
        var options = DesktopOptions.FromArgs(args, _directory);

        Assert.Equal(expected, options.HardwareRequested);
        Assert.Equal(Path.Combine(_directory, "config", "solve.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(_directory, "logs"), options.LogDirectory);
    }

    // ==================== NoSignalSafetyInterlock ====================

    [Fact]
    public void NoSignalInterlock_AlwaysPermitsReset()
    {
        var interlock = new NoSignalSafetyInterlock(NullLogger.Instance);

        Assert.True(interlock.CanReset(BusId.New()).IsSuccess);
    }

    // ==================== SqliteAppSettingsStore 与 v2 迁移 ====================

    [Fact]
    public async Task SettingsStore_GetMissing_ReturnsNull()
    {
        var store = new SqliteAppSettingsStore(await OpenSeedDatabaseAsync(), NullLogger.Instance);

        var result = await store.GetAsync("not.exists", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task SettingsStore_SetThenOverwrite_ReturnsLatest()
    {
        var store = new SqliteAppSettingsStore(await OpenSeedDatabaseAsync(), NullLogger.Instance);

        Assert.True((await store.SetAsync("k", "1", CancellationToken.None)).IsSuccess);
        Assert.True((await store.SetAsync("k", "2", CancellationToken.None)).IsSuccess);

        Assert.Equal("2", (await store.GetAsync("k", CancellationToken.None)).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task SettingsStore_InvalidKey_Throws(string? key)
    {
        var store = new SqliteAppSettingsStore(await OpenSeedDatabaseAsync(), NullLogger.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetAsync(key!, CancellationToken.None));
    }

    [Fact]
    public async Task SettingsStore_KeyTooLong_Throws()
    {
        var store = new SqliteAppSettingsStore(await OpenSeedDatabaseAsync(), NullLogger.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SetAsync(new string('k', SqliteAppSettingsStore.MaxKeyLength + 1), "v", CancellationToken.None));
    }

    [Fact]
    public async Task Migration_V1ToV2_AddsSettingsTable_AndKeepsBuses()
    {
        var descriptor = Tcp();
        await SeedAsync(null, descriptor);

        // 把库退回 v1 形态（去掉 v2 及之后新增的表）
        var database = await OpenSeedDatabaseAsync();
        await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync("DROP TABLE topology_nodes; DROP TABLE app_settings; PRAGMA user_version = 1;");
        }
        SqliteConnection.ClearAllPools();

        var reopened = await SqliteDatabase.OpenAsync(new SqliteStorageOptions(DatabasePath), NullLogger.Instance, CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.ToString());
        await using (var connection = await reopened.Value.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(SqliteDatabase.LatestSchemaVersion, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        }

        var store = new SqliteAppSettingsStore(reopened.Value, NullLogger.Instance);
        Assert.True((await store.SetAsync(TransportModePolicy.HardwareEnabledKey, "false", CancellationToken.None)).IsSuccess);

        var snapshot = (await new SqliteBusConfigurationRepository(reopened.Value, NullLogger.Instance)
            .LoadAllAsync(CancellationToken.None)).Value;
        Assert.Equal(descriptor, Assert.Single(snapshot.Descriptors));
    }

    // ==================== AppHost 启动 ====================

    [Fact]
    public async Task Start_EmptyFolder_CreatesSchemaV2_InSimulation()
    {
        var host = CreateHost();

        var report = await host.Start();

        Assert.True(report.Succeeded, report.Fatal?.ToString());
        Assert.Equal(TransportMode.Simulation, report.Mode!.Mode);
        Assert.Equal(0, report.RegisteredCount);
        Assert.NotNull(host.Buses);
        Assert.True(File.Exists(DatabasePath));

        // 尚未建档：拓扑服务可用、没有拓扑、没有错误
        Assert.NotNull(host.Topology);
        Assert.Null(host.Topology.Current);
        Assert.Null(host.Topology.LoadError);
    }

    [Fact]
    public async Task Start_LoadsSavedTopology_WithItsIds()
    {
        var saved = EquipmentTopology.Create(new TopologyLayout(1, 1, 1, 2, 2).ToDefinition("老化房").Value).Value;
        Assert.True((await new SqliteTopologyRepository(await OpenSeedDatabaseAsync(), NullLogger.Instance)
            .SaveAsync(saved, CancellationToken.None)).IsSuccess);
        SqliteConnection.ClearAllPools();
        var host = CreateHost();

        var report = await host.Start();

        Assert.True(report.Succeeded);
        var loaded = host.Topology!.Current!;
        Assert.Equal(saved.Root.Id, loaded.Root.Id);
        Assert.Equal(saved.GetNode(TopologyAddress.Of(1, 1, 1, 2, 2)).Value.Id,
            loaded.GetNode(TopologyAddress.Of(1, 1, 1, 2, 2)).Value.Id);
    }

    [Fact]
    public async Task Start_TwinFollowsTopology_AndShowsNoDataWithoutASource()
    {
        var saved = EquipmentTopology.Create(new TopologyLayout(1, 1, 1, 1, 4).ToDefinition("老化房").Value).Value;
        Assert.True((await new SqliteTopologyRepository(await OpenSeedDatabaseAsync(), NullLogger.Instance)
            .SaveAsync(saved, CancellationToken.None)).IsSuccess);
        SqliteConnection.ClearAllPools();
        var host = CreateHost();

        var report = await host.Start();

        Assert.Equal(TransportMode.Simulation, report.Mode!.Mode);
        var twin = host.Twin!;
        Assert.Equal(saved.Root.Id, twin.Topology!.Root.Id);
        Assert.Equal(4, twin.ProductIds.Count);

        // 不运行任何假数据：等过一个仿真周期（250 ms）的时长，产品位仍全部是"无数据"
        await Task.Delay(600);
        Assert.Equal(4, twin.GetSummary(saved.Root.Id)[Core.Domain.Twin.ProductState.Unknown]);
    }

    [Fact]
    public async Task Start_CorruptTopology_DisablesTopology_ButBusesStillStart()
    {
        await SeedAsync(null, Tcp("A", 50_511));
        var database = await OpenSeedDatabaseAsync();
        var topology = EquipmentTopology.Create(new TopologyLayout(1, 1, 1, 1, 1).ToDefinition("老化房").Value).Value;
        Assert.True((await new SqliteTopologyRepository(database, NullLogger.Instance).SaveAsync(topology, CancellationToken.None)).IsSuccess);
        await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync("UPDATE topology_nodes SET address = 'C09' WHERE address = 'C01';");
        }
        SqliteConnection.ClearAllPools();
        var host = CreateHost();

        var report = await host.Start();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.RegisteredCount);
        Assert.Null(host.Topology!.Current);
        Assert.Equal(TopologyErrors.StorageCorruptCode, host.Topology.LoadError!.Code);
    }

    [Fact]
    public async Task Start_RegistersSeededBuses_WithoutConnecting()
    {
        await SeedAsync(null, Tcp("A", 50_501), Tcp("B", 50_502));
        var host = CreateHost();

        var report = await host.Start();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.RegisteredCount);
        Assert.Equal(2, host.Buses!.Buses.Count);
        Assert.All(host.Buses.Buses, bus => Assert.Equal(BusState.Disconnected, bus.State));
    }

    [Fact]
    public async Task Start_HardwareRequested_ButNotEnabledInDatabase_UsesSimulation()
    {
        await SeedAsync("false", Tcp());
        var host = CreateHost(hardwareRequested: true);

        var report = await host.Start();

        Assert.Equal(TransportMode.Simulation, report.Mode!.Mode);
        Assert.Equal(1, report.RegisteredCount);
    }

    [Fact]
    public async Task Start_EnabledInDatabase_ButNoArgument_UsesSimulation()
    {
        await SeedAsync("true", Tcp());
        var host = CreateHost(hardwareRequested: false);

        var report = await host.Start();

        Assert.Equal(TransportMode.Simulation, report.Mode!.Mode);
    }

    [Fact]
    public async Task Start_BothConditions_UsesHardware_StillNotConnected()
    {
        await SeedAsync("true", Tcp());
        var host = CreateHost(hardwareRequested: true);

        var report = await host.Start();

        Assert.True(report.Succeeded);
        Assert.Equal(TransportMode.Hardware, report.Mode!.Mode);
        Assert.Equal(BusState.Disconnected, Assert.Single(host.Buses!.Buses).State);
    }

    [Fact]
    public async Task Start_CorruptRow_IsReported_OthersRegistered()
    {
        var good = Tcp("good", 50_501);
        var bad = Tcp("bad", 50_502);
        await SeedAsync(null, good, bad);
        var database = await OpenSeedDatabaseAsync();
        await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync("UPDATE buses SET port = 0 WHERE id = @Id;", new { Id = bad.Id.ToString() });
        }
        SqliteConnection.ClearAllPools();
        var host = CreateHost();

        var report = await host.Start();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.RegisteredCount);
        Assert.Single(report.ConfigurationIssues);
        Assert.True(host.Buses!.TryGet(good.Id, out _));
        Assert.False(host.Buses.TryGet(bad.Id, out _));
    }

    [Fact]
    public async Task Start_DatabaseUnavailable_IsFatal_AndCreatesNoBuses()
    {
        // 父路径是文件而不是目录：配置库必然打不开
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        await File.WriteAllTextAsync(blocker, "x");
        var host = CreateHost(databasePath: Path.Combine(blocker, "solve.db"));

        var report = await host.Start();

        Assert.False(report.Succeeded);
        Assert.Null(report.Mode);
        Assert.Null(host.Buses);
        Assert.Equal(0, host.EmergencyStop("测试"));
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var host = CreateHost();

        Assert.Same(host.Start(), host.Start());
        await host.Start();
    }

    [Fact]
    public async Task EmergencyStop_TripsAllBuses()
    {
        await SeedAsync(null, Tcp("A", 50_501), Tcp("B", 50_502));
        var host = CreateHost();
        await host.Start();

        Assert.Equal(2, host.EmergencyStop("系统急停"));
        Assert.All(host.Buses!.Buses, bus => Assert.Equal(BusState.SafeStop, bus.State));
    }

    // ==================== AppHost 关停 ====================

    [Fact]
    public async Task Dispose_ReleasesBuses_FlushesLog_AndRejectsStart()
    {
        await SeedAsync(null, Tcp());
        var host = CreateHost();
        await host.Start();

        await host.DisposeAsync();
        await host.DisposeAsync(); // 幂等

        Assert.Null(host.Buses);
        // Start 在调用线程上同步抛出，而不是返回失败的任务
        Assert.Throws<ObjectDisposedException>(() => { _ = host.Start(); });

        // 日志已刷写并关闭：文件存在且可以独占读取
        var log = Assert.Single(DailyLogFiles.List(LogDirectory)).Path;
        await using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        Assert.Contains("总线系统启动完成", text);
        Assert.Contains("程序退出", text);
    }

    [Fact]
    public async Task Dispose_DuringStartup_DoesNotLeakManager()
    {
        await SeedAsync(null, Tcp());
        var host = CreateHost();
        var startup = host.Start();

        await host.DisposeAsync();
        var report = await startup;

        // 无论启动是被中止还是已完成，释放后都不能留下仍存活的注册表
        Assert.Null(host.Buses);
        Assert.True(report.Succeeded || report.Fatal!.Code == "App.StartupCancelled", report.Fatal?.ToString());
    }

    [Fact]
    public void Shutdown_WithoutStart_CompletesWithinTimeout()
    {
        var host = CreateHost();

        Assert.True(host.Shutdown(TimeSpan.FromSeconds(5)));
    }
}
