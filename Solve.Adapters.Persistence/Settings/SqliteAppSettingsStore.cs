using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Persistence.Settings;

/// <summary>
/// 基于 app_settings 表（结构版本 v2）的键值设置存储
/// </summary>
public sealed class SqliteAppSettingsStore : IAppSettingsStore
{
    public const int MaxKeyLength = 128;

    private const string SelectSql = "SELECT value FROM app_settings WHERE key = @Key;";

    private const string UpsertSql = """
        INSERT INTO app_settings (key, value, updated_at) VALUES (@Key, @Value, @UpdatedAt)
        ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
        """;

    private readonly SqliteDatabase _database;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public SqliteAppSettingsStore(SqliteDatabase database, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<string?>> GetAsync(string key, CancellationToken cancellationToken)
    {
        EnsureValidKey(key);

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var value = await connection.QuerySingleOrDefaultAsync<string?>(
                _database.Command(SelectSql, cancellationToken, new { Key = key })).ConfigureAwait(false);
            return Result<string?>.Success(value);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "读取设置 {Key} 失败", key);
            return Result<string?>.Failure(SettingsErrors.StorageFailed($"读取 {key}（SQLite {ex.SqliteErrorCode}）：{ex.Message}"));
        }
    }

    public async Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        EnsureValidKey(key);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(_database.Command(UpsertSql, cancellationToken, new
            {
                Key = key,
                Value = value,
                UpdatedAt = _time.GetUtcNow().UtcDateTime.ToString("O"),
            })).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "写入设置 {Key} 失败", key);
            return Result.Failure(SettingsErrors.StorageFailed($"写入 {key}（SQLite {ex.SqliteErrorCode}）：{ex.Message}"));
        }

        // 设置变更属于重要操作，记入黑匣子
        _logger.LogWarning("设置已变更 {Key} = {Value}", key, value);
        return Result.Success();
    }

    // 键由代码常量提供，非法键属于编程错误
    private static void EnsureValidKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, MaxKeyLength, nameof(key));
    }
}
