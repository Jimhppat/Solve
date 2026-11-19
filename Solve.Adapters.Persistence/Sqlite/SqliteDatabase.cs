using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Persistence.Sqlite;

/// <summary>
/// 配置库：负责文件位置、连接参数、日志模式与结构版本迁移。各仓储通过它获取连接。
/// </summary>
/// <remarks>
/// <para>持久性：WAL 日志 + synchronous=FULL。现场突然断电时，已提交的配置不会丢失或损坏。</para>
/// <para>版本：使用 PRAGMA user_version 记录结构版本。库版本高于程序支持版本时拒绝打开，
/// 防止旧程序误改新结构的数据。</para>
/// </remarks>
public sealed class SqliteDatabase
{
    /// <summary>
    /// 结构迁移脚本，下标 + 1 即目标版本号。只允许追加，已发布的脚本不得修改。
    /// </summary>
    private static readonly string[] Migrations =
    [
        // v1：总线配置。单表 + kind 鉴别列；CHECK 保证每种总线只填自己的链路字段。
        // 数值范围由领域层校验（单一事实来源），数据库只保证结构完整。
        """
        CREATE TABLE buses (
            id                          TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
            kind                        TEXT    NOT NULL CHECK (kind IN ('serial', 'tcp')),
            name                        TEXT    NOT NULL,
            connect_timeout_ms          INTEGER NOT NULL,
            read_timeout_ms             INTEGER NOT NULL,
            write_timeout_ms            INTEGER NOT NULL,
            reconnect_initial_delay_ms  INTEGER NOT NULL,
            reconnect_max_delay_ms      INTEGER NOT NULL,
            reconnect_multiplier        REAL    NOT NULL,
            reconnect_max_attempts      INTEGER NULL,      -- NULL 表示无限重连
            port_name                   TEXT    NULL,
            baud_rate                   INTEGER NULL,
            data_bits                   INTEGER NULL,
            parity                      TEXT    NULL CHECK (parity IN ('None', 'Odd', 'Even', 'Mark', 'Space')),
            stop_bits                   TEXT    NULL CHECK (stop_bits IN ('One', 'Two', 'OnePointFive')),
            host                        TEXT    NULL,
            port                        INTEGER NULL,
            created_at                  TEXT    NOT NULL,  -- ISO 8601 UTC
            updated_at                  TEXT    NOT NULL,  -- ISO 8601 UTC
            CHECK (
                (kind = 'serial'
                    AND port_name IS NOT NULL AND baud_rate IS NOT NULL AND data_bits IS NOT NULL
                    AND parity IS NOT NULL AND stop_bits IS NOT NULL
                    AND host IS NULL AND port IS NULL)
             OR (kind = 'tcp'
                    AND host IS NOT NULL AND port IS NOT NULL
                    AND port_name IS NULL AND baud_rate IS NULL AND data_bits IS NULL
                    AND parity IS NULL AND stop_bits IS NULL)
            )
        ) STRICT;

        -- 同一串口只能属于一条总线（忽略大小写与首尾空格，与 BusManager 的冲突判定一致）
        CREATE UNIQUE INDEX ux_buses_serial_port
            ON buses (trim(port_name) COLLATE NOCASE)
            WHERE kind = 'serial';
        """,

        // v2：应用级键值设置（如 bus.hardware_enabled）。只新增表，不改动 v1 数据。
        """
        CREATE TABLE app_settings (
            key         TEXT NOT NULL PRIMARY KEY CHECK (length(key) BETWEEN 1 AND 128),
            value       TEXT NOT NULL,
            updated_at  TEXT NOT NULL   -- ISO 8601 UTC
        ) STRICT;
        """,

        // v3：设备六层拓扑（系统 / 柜体 / 层位 / 槽位 / 治具位 / 产品位），每行一个节点（邻接表）。只新增表，不改动既有数据。
        // id 是节点的系统身份（UUID），首次建档生成后终身不变；address 是人用位置编码（C01-L02-…），
        // 冗余存储便于直接查库，加载时与按树重算的结果核对。整棵树在单事务内以快照方式整体替换。
        // 将来引用本表的外键必须使用默认的 NO ACTION（不得 CASCADE / RESTRICT）：
        // 保存时 defer_foreign_keys 把检查推迟到提交，仍被引用的节点无法被删除，快照替换也不会误删引用方。
        """
        CREATE TABLE topology_nodes (
            id          TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
            parent_id   TEXT    NULL REFERENCES topology_nodes (id),
            level       INTEGER NOT NULL CHECK (level BETWEEN 1 AND 6),
            ordinal     INTEGER NOT NULL,
            address     TEXT    NOT NULL,
            name        TEXT    NULL,
            enabled     INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            created_at  TEXT    NOT NULL,  -- ISO 8601 UTC
            updated_at  TEXT    NOT NULL,  -- ISO 8601 UTC
            -- 系统根（level 1）是唯一没有上级的节点
            CHECK ((level = 1) = (parent_id IS NULL))
        ) STRICT;

        -- 只允许一个系统根
        CREATE UNIQUE INDEX ux_topology_nodes_root
            ON topology_nodes (level)
            WHERE parent_id IS NULL;

        -- 同一上级下序号唯一；兼作外键子表索引
        CREATE UNIQUE INDEX ux_topology_nodes_sibling
            ON topology_nodes (parent_id, ordinal)
            WHERE parent_id IS NOT NULL;

        -- 人用位置编码唯一，支持按编码直接查库
        CREATE UNIQUE INDEX ux_topology_nodes_address
            ON topology_nodes (address);
        """,

        // v4：总线挂载设备。只新增表，不改动既有数据；与 v1 一样，数值范围由领域层校验，数据库只保证结构完整。
        // bus_id 使用 ON DELETE CASCADE：删除总线时其下设备在同一条 DELETE 语句内一并删除，不会留下孤儿设备。
        // 因此 buses 表的保存必须一直用 ON CONFLICT DO UPDATE，不得改成 INSERT OR REPLACE 或"先删后插"，否则保存总线会清空其设备。
        """
        CREATE TABLE bus_devices (
            id          TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36),
            bus_id      TEXT    NOT NULL REFERENCES buses (id) ON DELETE CASCADE,
            address     INTEGER NOT NULL,
            name        TEXT    NOT NULL,
            remark      TEXT    NULL,
            created_at  TEXT    NOT NULL,  -- ISO 8601 UTC
            updated_at  TEXT    NOT NULL   -- ISO 8601 UTC
        ) STRICT;

        -- 同一条总线上地址唯一；兼作外键子表索引（级联删除按 bus_id 查找）
        CREATE UNIQUE INDEX ux_bus_devices_address
            ON bus_devices (bus_id, address);
        """,
    ];

    public static int LatestSchemaVersion => Migrations.Length;

    private readonly string _connectionString;

    private SqliteDatabase(string databasePath, int commandTimeoutSeconds)
    {
        DatabasePath = databasePath;
        CommandTimeoutSeconds = commandTimeoutSeconds;
        _connectionString = BuildConnectionString(databasePath, commandTimeoutSeconds);
    }

    public string DatabasePath { get; }

    public int CommandTimeoutSeconds { get; }

    /// <summary>
    /// 创建目录、启用 WAL、执行结构迁移。任一步失败返回 Bus.Storage.Failed，不抛异常。
    /// </summary>
    public static async Task<Result<SqliteDatabase>> OpenAsync(
        SqliteStorageOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CommandTimeoutSeconds, 1);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(options.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result<SqliteDatabase>.Failure(BusErrors.StorageFailed($"无法创建配置目录 {options.DatabasePath}：{ex.Message}"));
        }

        var database = new SqliteDatabase(fullPath, options.CommandTimeoutSeconds);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // journal_mode 持久保存在库文件中，只需设置一次；必须在事务外执行
            var mode = await connection.ExecuteScalarAsync<string>(
                database.Command("PRAGMA journal_mode = WAL;", cancellationToken)).ConfigureAwait(false);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning("配置库 {Path} 无法启用 WAL，当前日志模式 {Mode}", fullPath, mode);

            var migrated = await MigrateAsync(database, connection, logger, cancellationToken).ConfigureAwait(false);
            if (migrated.IsFailure)
                return Result<SqliteDatabase>.Failure(migrated.Error);
        }
        catch (SqliteException ex)
        {
            return Result<SqliteDatabase>.Failure(BusErrors.StorageFailed($"打开配置库 {fullPath} 失败（SQLite {ex.SqliteErrorCode}）：{ex.Message}"));
        }

        logger.LogInformation("配置库就绪 {Path}（结构版本 v{Version}）", fullPath, LatestSchemaVersion);
        return Result<SqliteDatabase>.Success(database);
    }

    /// <summary>
    /// 打开一个已配置好会话参数的连接，调用方负责释放（连接池会复用底层句柄）。
    /// </summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 会话级参数，每个连接都要设置
            await connection.ExecuteAsync(Command("PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;", cancellationToken))
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public CommandDefinition Command(string sql, CancellationToken cancellationToken, object? parameters = null, SqliteTransaction? transaction = null)
        => new(sql, parameters, transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken);

    /// <summary>
    /// 关闭指定配置库在连接池中的句柄：空闲连接立即关闭，使用中的连接归还时关闭、不再入池。
    /// 只影响该库文件，不触碰进程内其他库的连接池。用于移动 / 删除库文件之前（例如测试清理临时目录）。
    /// </summary>
    /// <remarks>
    /// <para>不要改用进程级的 <see cref="SqliteConnection.ClearAllPools"/>：它会波及进程内所有库的连接池。
    /// Microsoft.Data.Sqlite 10.0.13 之前它还会把其他线程"刚从池中取出、尚未登记所有者"的连接误判为泄漏并关闭其原生句柄
    /// （dotnet/efcore#39008），曾使并行测试偶发 ObjectDisposedException。依赖已升级到修复版本，按库清池仍是正确的隔离边界。</para>
    /// <para>应在本库的使用者都结束后调用：使用中的连接要等归还时才关闭，在此之前文件句柄仍被占用。</para>
    /// </remarks>
    /// <param name="options">必须与打开该库时的参数一致：连接池按连接字符串分组，参数不同即是另一个池。</param>
    public static void ClearPool(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);

        // 未打开的连接已能定位到所属的池组，不会创建或打开库文件
        using var connection = new SqliteConnection(
            BuildConnectionString(Path.GetFullPath(options.DatabasePath), options.CommandTimeoutSeconds));
        SqliteConnection.ClearPool(connection);
    }

    // 构造函数与 ClearPool 共用：两处字符串只要差一个字符，清掉的就是另一个空池，句柄仍被占用
    private static string BuildConnectionString(string fullPath, int commandTimeoutSeconds)
        => new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = commandTimeoutSeconds,
            Pooling = true,
        }.ToString();

    private static async Task<Result> MigrateAsync(
        SqliteDatabase database,
        SqliteConnection connection,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var current = await connection.ExecuteScalarAsync<long>(database.Command("PRAGMA user_version;", cancellationToken))
            .ConfigureAwait(false);

        if (current > LatestSchemaVersion)
        {
            return Result.Failure(BusErrors.StorageFailed(
                $"配置库结构版本 v{current} 高于本程序支持的 v{LatestSchemaVersion}，为防止数据损坏拒绝访问，请升级程序。"));
        }

        for (var version = (int)current + 1; version <= LatestSchemaVersion; version++)
        {
            // 每个版本一个事务：脚本与版本号要么一起生效，要么都不生效
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(database.Command(Migrations[version - 1], cancellationToken, transaction: transaction))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(database.Command($"PRAGMA user_version = {version};", cancellationToken, transaction: transaction))
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("配置库结构迁移 v{From} → v{To}", version - 1, version);
        }

        return Result.Success();
    }
}
