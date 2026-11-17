namespace Solve.Adapters.Persistence.Sqlite;

/// <summary>
/// SQLite 配置库参数
/// </summary>
/// <param name="DatabasePath">数据库文件路径（目录不存在时自动创建）</param>
public sealed record SqliteStorageOptions(string DatabasePath)
{
    /// <summary>
    /// 默认位置：程序目录下的 config/solve.db（开发期即 Solve.Desktop/bin/Debug/net10.0/config/solve.db）。
    /// </summary>
    /// <remarks>
    /// 安装到 Program Files 等只读目录后程序目录不可写，届时应改为 %ProgramData% 下的路径。
    /// </remarks>
    public static SqliteStorageOptions Default { get; } =
        new(Path.Combine(AppContext.BaseDirectory, "config", "solve.db"));

    /// <summary>
    /// 单条命令超时（秒）。Microsoft.Data.Sqlite 在数据库被锁（SQLITE_BUSY）时会在此时间内重试。
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 5;
}
