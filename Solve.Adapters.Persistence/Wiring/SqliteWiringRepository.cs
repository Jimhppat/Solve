using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Wiring.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Wiring;

namespace Solve.Adapters.Persistence.Wiring;

/// <summary>
/// 基于 topology_wires 表（结构版本 v6）的接线仓储。
/// </summary>
/// <remarks>
/// 每次操作使用独立连接（连接池复用句柄），无共享可变状态，可被多线程并发调用。
/// 通道独占由唯一索引保证：并发接同一路通道时只有一个成功，不依赖先查后写。
/// </remarks>
public sealed class SqliteWiringRepository : IWiringRepository
{
    // SQLite 主错误码 SQLITE_CONSTRAINT 及其扩展码
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintForeignKey = 787;

    private const string SelectAllSql = """
        SELECT id            AS Id,
               node_id       AS NodeId,
               device_id     AS DeviceId,
               channel_group AS ChannelGroup,
               channel_index AS ChannelIndex,
               kind          AS Kind,
               purpose       AS Purpose,
               label         AS Label,
               created_at    AS CreatedAt,
               updated_at    AS UpdatedAt
          FROM topology_wires
         ORDER BY created_at, id;
        """;

    private const string InsertSql = """
        INSERT INTO topology_wires (id, node_id, device_id, channel_group, channel_index, kind, purpose, label, created_at, updated_at)
        VALUES (@Id, @NodeId, @DeviceId, @ChannelGroup, @ChannelIndex, @Kind, @Purpose, @Label, @CreatedAt, @UpdatedAt);
        """;

    // 落点、通道、功能只作条件不作更新：改接到别处等于拆除后重新接线
    private const string UpdateSql = """
        UPDATE topology_wires
           SET purpose    = @Purpose,
               label      = @Label,
               updated_at = @UpdatedAt
         WHERE id = @Id AND node_id = @NodeId AND device_id = @DeviceId
           AND channel_group = @ChannelGroup AND channel_index = @ChannelIndex;
        """;

    private const string DeleteSql = "DELETE FROM topology_wires WHERE id = @Id;";

    private readonly SqliteDatabase _database;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public SqliteWiringRepository(SqliteDatabase database, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<IReadOnlyList<Wire>>> LoadAllAsync(CancellationToken cancellationToken)
    {
        List<WireRow> rows;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            rows = (await connection.QueryAsync<WireRow>(_database.Command(SelectAllSql, cancellationToken)).ConfigureAwait(false)).AsList();
        }
        catch (SqliteException ex)
        {
            return Result<IReadOnlyList<Wire>>.Failure(StorageFailure("读取接线", ex));
        }

        var wires = new List<Wire>(rows.Count);
        foreach (var row in rows)
        {
            var mapped = WireRowMapper.ToWire(row, out var problems);
            if (mapped.IsSuccess)
            {
                wires.Add(mapped.Value);
                continue;
            }

            // 无效记录不参与接线（避免把读数或命令关联到错误的位置），但必须留痕供人工修复
            _logger.LogError("接线记录 {RowId} 无效，已跳过：{Problems}", row.Id, string.Join("；", problems.Select(p => p.ToString())));
        }

        return Result<IReadOnlyList<Wire>>.Success(wires);
    }

    public async Task<Result> AddRangeAsync(IReadOnlyList<Wire> wires, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wires);
        if (wires.Count == 0)
            return Result.Success();

        var errors = wires.SelectMany(w => w.Validate()).ToList();
        if (errors.Count > 0)
            return Result.Failure(WiringErrors.InvalidWire(errors));

        var now = _time.GetUtcNow();
        var rows = wires.Select(w => WireRowMapper.ToRow(w, now)).ToList();
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // 一批在同一事务内：任一行违反约束则整体回滚，不会留下半批接线
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(_database.Command(InsertSql, cancellationToken, rows, transaction)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsConstraint(ex, SqliteConstraintUnique) || IsConstraint(ex, SqliteConstraintForeignKey))
        {
            _logger.LogWarning("新增 {Count} 条接线被约束拒绝（通道已占用，或位置、模块已不存在）：SQLite {Code}/{ExtendedCode}",
                wires.Count, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
            return Result.Failure(WiringErrors.StorageConflict());
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"新增 {wires.Count} 条接线", ex));
        }

        return Result.Success();
    }

    public async Task<Result> UpdateAsync(Wire wire, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);

        var errors = wire.Validate();
        if (errors.Count > 0)
            return Result.Failure(WiringErrors.InvalidWire(errors));

        var row = WireRowMapper.ToRow(wire, _time.GetUtcNow());
        int affected;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            affected = await connection.ExecuteAsync(_database.Command(UpdateSql, cancellationToken, row)).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"修改{wire}", ex));
        }

        return affected == 0 ? Result.Failure(WiringErrors.StorageNotFound()) : Result.Success();
    }

    public async Task<Result<int>> DeleteRangeAsync(IReadOnlyList<WireId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return Result<int>.Success(0);

        var deleted = 0;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // 一批在同一事务内：中途出错整体回滚，不会只拆掉一部分
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                deleted += await connection.ExecuteAsync(
                    _database.Command(DeleteSql, cancellationToken, new { Id = id.ToString() }, transaction)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return Result<int>.Failure(StorageFailure($"拆除 {ids.Count} 条接线", ex));
        }

        return Result<int>.Success(deleted);
    }

    private static bool IsConstraint(SqliteException ex, int extendedCode)
        => ex.SqliteErrorCode == SqliteConstraint && ex.SqliteExtendedErrorCode == extendedCode;

    private Error StorageFailure(string operation, SqliteException ex)
    {
        _logger.LogError(ex, "{Operation}失败：SQLite {Code}/{ExtendedCode}", operation, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
        return WiringErrors.StorageFailed($"{operation}失败（SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}）：{ex.Message}");
    }
}
