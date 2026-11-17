using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Persistence.Buses;

/// <summary>
/// 基于 SQLite + Dapper 的总线配置仓储。
/// </summary>
/// <remarks>
/// 每次操作使用独立连接（连接池复用句柄），无共享可变状态，可被多线程并发调用。
/// Microsoft.Data.Sqlite 的 Async 方法内部是同步 I/O，调用方不要在 UI / 控制线程上直接等待。
/// </remarks>
public sealed class SqliteBusConfigurationRepository : IBusConfigurationRepository
{
    // SQLite 主错误码 SQLITE_CONSTRAINT 与扩展码 SQLITE_CONSTRAINT_UNIQUE
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;

    // 列名统一别名为属性名，避免修改 Dapper 全局的下划线匹配设置
    private const string SelectAllSql = """
        SELECT id                         AS Id,
               kind                       AS Kind,
               name                       AS Name,
               connect_timeout_ms         AS ConnectTimeoutMs,
               read_timeout_ms            AS ReadTimeoutMs,
               write_timeout_ms           AS WriteTimeoutMs,
               reconnect_initial_delay_ms AS ReconnectInitialDelayMs,
               reconnect_max_delay_ms     AS ReconnectMaxDelayMs,
               reconnect_multiplier       AS ReconnectMultiplier,
               reconnect_max_attempts     AS ReconnectMaxAttempts,
               port_name                  AS PortName,
               baud_rate                  AS BaudRate,
               data_bits                  AS DataBits,
               parity                     AS Parity,
               stop_bits                  AS StopBits,
               host                       AS Host,
               port                       AS Port,
               created_at                 AS CreatedAt,
               updated_at                 AS UpdatedAt
          FROM buses
         ORDER BY created_at, id;
        """;

    // 更新时保留 created_at，只刷新 updated_at；kind 变化时另一类链路字段被置 NULL，满足表级 CHECK
    private const string UpsertSql = """
        INSERT INTO buses (
            id, kind, name,
            connect_timeout_ms, read_timeout_ms, write_timeout_ms,
            reconnect_initial_delay_ms, reconnect_max_delay_ms, reconnect_multiplier, reconnect_max_attempts,
            port_name, baud_rate, data_bits, parity, stop_bits,
            host, port,
            created_at, updated_at)
        VALUES (
            @Id, @Kind, @Name,
            @ConnectTimeoutMs, @ReadTimeoutMs, @WriteTimeoutMs,
            @ReconnectInitialDelayMs, @ReconnectMaxDelayMs, @ReconnectMultiplier, @ReconnectMaxAttempts,
            @PortName, @BaudRate, @DataBits, @Parity, @StopBits,
            @Host, @Port,
            @CreatedAt, @UpdatedAt)
        ON CONFLICT (id) DO UPDATE SET
            kind                       = excluded.kind,
            name                       = excluded.name,
            connect_timeout_ms         = excluded.connect_timeout_ms,
            read_timeout_ms            = excluded.read_timeout_ms,
            write_timeout_ms           = excluded.write_timeout_ms,
            reconnect_initial_delay_ms = excluded.reconnect_initial_delay_ms,
            reconnect_max_delay_ms     = excluded.reconnect_max_delay_ms,
            reconnect_multiplier       = excluded.reconnect_multiplier,
            reconnect_max_attempts     = excluded.reconnect_max_attempts,
            port_name                  = excluded.port_name,
            baud_rate                  = excluded.baud_rate,
            data_bits                  = excluded.data_bits,
            parity                     = excluded.parity,
            stop_bits                  = excluded.stop_bits,
            host                       = excluded.host,
            port                       = excluded.port,
            updated_at                 = excluded.updated_at;
        """;

    private const string DeleteSql = "DELETE FROM buses WHERE id = @Id;";

    private readonly SqliteDatabase _database;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public SqliteBusConfigurationRepository(SqliteDatabase database, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<BusConfigurationSnapshot>> LoadAllAsync(CancellationToken cancellationToken)
    {
        List<BusRow> rows;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            rows = (await connection.QueryAsync<BusRow>(_database.Command(SelectAllSql, cancellationToken)).ConfigureAwait(false))
                .AsList();
        }
        catch (SqliteException ex)
        {
            return Result<BusConfigurationSnapshot>.Failure(StorageFailure("读取总线配置", ex));
        }

        var descriptors = new List<BusDescriptor>(rows.Count);
        var issues = new List<BusConfigurationIssue>();

        foreach (var row in rows)
        {
            var mapped = BusRowMapper.ToDescriptor(row, out var problems);
            if (mapped.IsSuccess)
            {
                descriptors.Add(mapped.Value);
                continue;
            }

            // 无效配置不加载为总线（避免以错误参数连接设备），但必须让人看见
            issues.Add(new BusConfigurationIssue(row.Id, problems));
            _logger.LogError("总线配置 {RowId} 无效，已跳过：{Problems}",
                row.Id, string.Join("；", problems.Select(p => p.ToString())));
        }

        _logger.LogInformation("加载总线配置 {Valid} 条，无效 {Invalid} 条", descriptors.Count, issues.Count);
        return Result<BusConfigurationSnapshot>.Success(new BusConfigurationSnapshot(descriptors, issues));
    }

    public async Task<Result> SaveAsync(BusDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result.Failure(BusErrors.InvalidDescriptor(errors));

        var row = BusRowMapper.ToRow(descriptor, _time.GetUtcNow());
        if (row.IsFailure)
            return Result.Failure(row.Error);

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(_database.Command(UpsertSql, cancellationToken, row.Value)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint && ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            return Result.Failure(BusErrors.StorageConflict($"串口 {row.Value.PortName} 已被其他总线配置占用。"));
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"保存总线 {descriptor.Id}", ex));
        }

        _logger.LogInformation("保存总线配置 {BusId} {Name} ({Kind})", descriptor.Id, descriptor.Name, row.Value.Kind);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(BusId id, CancellationToken cancellationToken)
    {
        int affected;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            affected = await connection.ExecuteAsync(
                _database.Command(DeleteSql, cancellationToken, new { Id = id.Value.ToString("D") })).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"删除总线 {id}", ex));
        }

        if (affected == 0)
            return Result.Failure(BusErrors.StorageNotFound(id));

        _logger.LogInformation("删除总线配置 {BusId}", id);
        return Result.Success();
    }

    private Error StorageFailure(string operation, SqliteException ex)
    {
        _logger.LogError(ex, "{Operation}失败：SQLite {Code}/{ExtendedCode}", operation, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
        return BusErrors.StorageFailed($"{operation}失败（SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}）：{ex.Message}");
    }
}
