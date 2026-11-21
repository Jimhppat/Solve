using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Topology.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Topology;

namespace Solve.Adapters.Persistence.Topology;

/// <summary>
/// 基于 topology_nodes 表（结构版本 v3）的设备拓扑仓储；保存时一并清理落点已删除的接线（topology_wires，v6）。
/// </summary>
/// <remarks>
/// <para>快照语义：保存 = 单事务内"清空 + 全量写入"。快照替换天然支持重新编号、整棵子树搬迁、扩容缩容，
/// 不会在中间状态撞上"同级序号唯一"约束；事务失败时库内保持原快照。</para>
/// <para>每次操作使用独立连接（连接池复用句柄），无共享可变状态，可被多线程并发调用；并发保存为"后写者生效"。
/// Microsoft.Data.Sqlite 的 Async 方法内部是同步 I/O，调用方不要在 UI / 控制线程上直接等待。</para>
/// </remarks>
public sealed class SqliteTopologyRepository : ITopologyRepository
{
    /// <summary>
    /// 删除节点时日志最多列出的样例数，其余只计数
    /// </summary>
    private const int MaxLoggedRemovals = 20;

    // 列名统一别名为属性名，避免修改 Dapper 全局的下划线匹配设置
    private const string SelectAllSql = """
        SELECT id         AS Id,
               parent_id  AS ParentId,
               level      AS Level,
               ordinal    AS Ordinal,
               address    AS Address,
               name       AS Name,
               enabled    AS Enabled,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt
          FROM topology_nodes
         ORDER BY level, address;
        """;

    // 外键检查推迟到提交：快照替换的中间状态允许暂时不一致，提交时整体校验（仅对当前事务有效）
    private const string DeferForeignKeysSql = "PRAGMA defer_foreign_keys = ON;";

    private const string DeleteAllSql = "DELETE FROM topology_nodes;";

    private const string InsertSql = """
        INSERT INTO topology_nodes (id, parent_id, level, ordinal, address, name, enabled, created_at, updated_at)
        VALUES (@Id, @ParentId, @Level, @Ordinal, @Address, @Name, @Enabled, @CreatedAt, @UpdatedAt);
        """;

    // 接线随位置一起删除：只删落点已不在新快照中的接线，仍存在的位置 Id 不变、接线原样保留。
    // 删除位置前界面已列出受影响的接线数并经操作员确认
    private const string DeleteDetachedWiresSql = """
        DELETE FROM topology_wires
         WHERE node_id NOT IN (SELECT id FROM topology_nodes);
        """;

    private readonly SqliteDatabase _database;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public SqliteTopologyRepository(SqliteDatabase database, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<EquipmentTopology?>> LoadAsync(CancellationToken cancellationToken)
    {
        List<TopologyNodeRow> rows;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            rows = (await connection.QueryAsync<TopologyNodeRow>(_database.Command(SelectAllSql, cancellationToken))
                .ConfigureAwait(false)).AsList();
        }
        catch (SqliteException ex)
        {
            return Result<EquipmentTopology?>.Failure(StorageFailure("读取设备拓扑", ex));
        }

        if (rows.Count == 0)
        {
            _logger.LogInformation("配置库中尚无设备拓扑");
            return Result<EquipmentTopology?>.Success(null);
        }

        var mapped = TopologyRowMapper.ToTopology(rows, out _);
        if (mapped.IsFailure)
        {
            // 残缺的孪生比没有孪生更危险（命令可能发往错位的节点），整体拒绝并留痕
            _logger.LogError("库内设备拓扑损坏，拒绝加载：{Error}", mapped.Error);
            return Result<EquipmentTopology?>.Failure(mapped.Error);
        }

        var topology = mapped.Value;
        _logger.LogInformation("加载设备拓扑 {Name} [{RootId}]：{NodeCount} 个节点，产品位 {ProductCount} 个",
            topology.Name, topology.Root.Id, topology.NodeCount, topology.CountAt(TopologyLevel.Product));
        return Result<EquipmentTopology?>.Success(topology);
    }

    public async Task<Result> SaveAsync(EquipmentTopology topology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var rows = TopologyRowMapper.ToRows(topology);
        var now = _time.GetUtcNow().UtcDateTime.ToString("O");
        var added = 0;
        var changed = 0;
        var removedWires = 0;
        List<TopologyNodeRow> removed;

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // IMMEDIATE：开始即取得写锁，读到的旧快照在提交前不会被其他写入者改动
            await using var transaction = connection.BeginTransaction(deferred: false);
            await connection.ExecuteAsync(_database.Command(DeferForeignKeysSql, cancellationToken, transaction: transaction))
                .ConfigureAwait(false);

            var existing = (await connection.QueryAsync<TopologyNodeRow>(
                    _database.Command(SelectAllSql, cancellationToken, transaction: transaction)).ConfigureAwait(false))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);

            // 已有节点保留创建时间；内容未变的节点保留修改时间，便于追溯"哪些位置在何时被改过"
            foreach (var row in rows)
            {
                if (existing.Remove(row.Id, out var previous))
                {
                    var same = row.HasSameContentAs(previous);
                    row.CreatedAt = previous.CreatedAt;
                    row.UpdatedAt = same ? previous.UpdatedAt : now;
                    if (!same)
                        changed++;
                }
                else
                {
                    row.CreatedAt = now;
                    row.UpdatedAt = now;
                    added++;
                }
            }

            removed = [.. existing.Values];

            await connection.ExecuteAsync(_database.Command(DeleteAllSql, cancellationToken, transaction: transaction))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(_database.Command(InsertSql, cancellationToken, rows, transaction))
                .ConfigureAwait(false);
            removedWires = await connection.ExecuteAsync(_database.Command(DeleteDetachedWiresSql, cancellationToken, transaction: transaction))
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"保存设备拓扑 {topology.Name}", ex));
        }

        _logger.LogInformation(
            "保存设备拓扑 {Name} [{RootId}]：共 {Total} 个节点，新增 {Added}，修改 {Changed}，删除 {Removed}",
            topology.Name, topology.Root.Id, rows.Count, added, changed, removed.Count);

        if (removed.Count > 0)
        {
            // 删除位置会让引用它的历史数据失去当前拓扑中的落点，必须留痕
            _logger.LogWarning("设备拓扑删除了 {Count} 个节点：{Removed}{More}",
                removed.Count,
                string.Join("，", removed.Take(MaxLoggedRemovals).Select(r => $"{r.Address}[{r.Id}]")),
                removed.Count > MaxLoggedRemovals ? $" 等（另有 {removed.Count - MaxLoggedRemovals} 个未列出）" : "");
        }

        if (removedWires > 0)
            _logger.LogWarning("设备拓扑删除位置时一并拆除了 {Count} 条接线", removedWires);

        return Result.Success();
    }

    private Error StorageFailure(string operation, SqliteException ex)
    {
        _logger.LogError(ex, "{Operation}失败：SQLite {Code}/{ExtendedCode}", operation, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
        return TopologyErrors.StorageFailed($"{operation}失败（SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}）：{ex.Message}");
    }
}
