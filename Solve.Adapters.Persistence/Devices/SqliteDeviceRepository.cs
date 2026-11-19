using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.Adapters.Persistence.Devices;

/// <summary>
/// 基于 SQLite + Dapper 的总线挂载设备仓储。
/// </summary>
/// <remarks>
/// 每次操作使用独立连接（连接池复用句柄），无共享可变状态，可被多线程并发调用。
/// 同一总线上地址唯一由唯一索引保证：并发保存同一地址时只有一个成功，不依赖先查后写。
/// </remarks>
public sealed class SqliteDeviceRepository : IDeviceRepository
{
    // SQLite 主错误码 SQLITE_CONSTRAINT 及其扩展码
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintForeignKey = 787;

    private const string SelectByBusSql = """
        SELECT id         AS Id,
               bus_id     AS BusId,
               driver_id  AS DriverId,
               address    AS Address,
               name       AS Name,
               remark     AS Remark,
               parameters AS Parameters,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt
          FROM bus_devices
         WHERE bus_id = @BusId
         ORDER BY address, id;
        """;

    private const string CountByBusSql = "SELECT count(*) FROM bus_devices WHERE bus_id = @BusId;";

    private const string InsertSql = """
        INSERT INTO bus_devices (id, bus_id, driver_id, address, name, remark, parameters, created_at, updated_at)
        VALUES (@Id, @BusId, @DriverId, @Address, @Name, @Remark, @Parameters, @CreatedAt, @UpdatedAt);
        """;

    // bus_id 只作条件不作更新：设备不在总线之间移动，传错总线时按"不存在"处理而不是悄悄换线
    private const string UpdateSql = """
        UPDATE bus_devices
           SET driver_id  = @DriverId,
               address    = @Address,
               name       = @Name,
               remark     = @Remark,
               parameters = @Parameters,
               updated_at = @UpdatedAt
         WHERE id = @Id AND bus_id = @BusId;
        """;

    private const string DeleteSql = "DELETE FROM bus_devices WHERE id = @Id;";

    private readonly SqliteDatabase _database;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public SqliteDeviceRepository(SqliteDatabase database, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<IReadOnlyList<DeviceDescriptor>>> ListByBusAsync(BusId busId, CancellationToken cancellationToken)
    {
        List<DeviceRow> rows;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            rows = (await connection.QueryAsync<DeviceRow>(
                    _database.Command(SelectByBusSql, cancellationToken, new { BusId = Text(busId) })).ConfigureAwait(false))
                .AsList();
        }
        catch (SqliteException ex)
        {
            return Result<IReadOnlyList<DeviceDescriptor>>.Failure(StorageFailure($"读取总线 {busId} 的模块", ex));
        }

        var devices = new List<DeviceDescriptor>(rows.Count);
        foreach (var row in rows)
        {
            var mapped = DeviceRowMapper.ToDescriptor(row, out var problems);
            if (mapped.IsSuccess)
            {
                devices.Add(mapped.Value);
                continue;
            }

            // 无效记录不显示为设备（避免以错误地址访问设备），但必须留痕供人工修复
            _logger.LogError("总线 {BusId} 上的模块配置 {RowId} 无效，已跳过：{Problems}",
                busId, row.Id, string.Join("；", problems.Select(p => p.ToString())));
        }

        return Result<IReadOnlyList<DeviceDescriptor>>.Success(devices);
    }

    public async Task<Result<int>> CountByBusAsync(BusId busId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var count = await connection.ExecuteScalarAsync<long>(
                _database.Command(CountByBusSql, cancellationToken, new { BusId = Text(busId) })).ConfigureAwait(false);
            return Result<int>.Success((int)count);
        }
        catch (SqliteException ex)
        {
            return Result<int>.Failure(StorageFailure($"统计总线 {busId} 的模块", ex));
        }
    }

    public async Task<Result> AddRangeAsync(IReadOnlyList<DeviceDescriptor> devices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
            return Result.Success();

        var errors = devices.SelectMany(d => d.Validate()).ToList();
        if (errors.Count > 0)
            return Result.Failure(DeviceErrors.InvalidDescriptor(errors));

        var now = _time.GetUtcNow();
        var rows = devices.Select(d => DeviceRowMapper.ToRow(d, now)).ToList();
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // 一批在同一事务内：任一行违反约束则整体回滚，不会留下半批模块
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                await connection.ExecuteAsync(_database.Command(InsertSql, cancellationToken, row, transaction))
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsConstraint(ex, SqliteConstraintUnique))
        {
            return Result.Failure(devices.Count == 1
                ? DeviceErrors.AddressConflict(devices[0].Address)
                : DeviceErrors.AddressConflict());
        }
        catch (SqliteException ex) when (IsConstraint(ex, SqliteConstraintForeignKey))
        {
            return Result.Failure(DeviceErrors.BusNotFound());
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"新增 {devices.Count} 个模块", ex));
        }

        foreach (var device in devices)
            _logger.LogInformation("总线 {BusId} 新增{Device} 驱动 {Driver}", device.BusId, device, device.Driver);

        return Result.Success();
    }

    public async Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var errors = device.Validate();
        if (errors.Count > 0)
            return Result.Failure(DeviceErrors.InvalidDescriptor(errors));

        var row = DeviceRowMapper.ToRow(device, _time.GetUtcNow());
        int affected;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            affected = await connection.ExecuteAsync(_database.Command(UpdateSql, cancellationToken, row)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsConstraint(ex, SqliteConstraintUnique))
        {
            return Result.Failure(DeviceErrors.AddressConflict(device.Address));
        }
        catch (SqliteException ex)
        {
            return Result.Failure(StorageFailure($"修改{device}", ex));
        }

        if (affected == 0)
            return Result.Failure(DeviceErrors.StorageNotFound());

        _logger.LogInformation("总线 {BusId} 修改{Device} 驱动 {Driver}", device.BusId, device, device.Driver);
        return Result.Success();
    }

    public async Task<Result<int>> DeleteRangeAsync(IReadOnlyList<DeviceId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return Result<int>.Success(0);

        var deleted = 0;
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // 一批在同一事务内：中途出错整体回滚，不会只删掉一部分
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
            return Result<int>.Failure(StorageFailure($"删除 {ids.Count} 个模块", ex));
        }

        return Result<int>.Success(deleted);
    }

    private static string Text(BusId busId) => busId.Value.ToString("D");

    private static bool IsConstraint(SqliteException ex, int extendedCode)
        => ex.SqliteErrorCode == SqliteConstraint && ex.SqliteExtendedErrorCode == extendedCode;

    private Error StorageFailure(string operation, SqliteException ex)
    {
        _logger.LogError(ex, "{Operation}失败：SQLite {Code}/{ExtendedCode}", operation, ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
        return DeviceErrors.StorageFailed($"{operation}失败（SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}）：{ex.Message}");
    }
}
