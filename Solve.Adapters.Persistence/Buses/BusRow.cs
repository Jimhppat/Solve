namespace Solve.Adapters.Persistence.Buses;

/// <summary>
/// buses 表的一行，只使用 SQLite 原生类型（INTEGER → long，REAL → double，TEXT → string）。
/// 领域类型与数据库类型在 <see cref="BusRowMapper"/> 中显式转换，不依赖 Dapper 隐式类型处理。
/// </summary>
internal sealed class BusRow
{
    public const string KindSerial = "serial";
    public const string KindTcp = "tcp";

    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    public long ConnectTimeoutMs { get; set; }

    public long ReadTimeoutMs { get; set; }

    public long WriteTimeoutMs { get; set; }

    public long ReconnectInitialDelayMs { get; set; }

    public long ReconnectMaxDelayMs { get; set; }

    public double ReconnectMultiplier { get; set; }

    public long? ReconnectMaxAttempts { get; set; }

    public string? PortName { get; set; }

    public long? BaudRate { get; set; }

    public long? DataBits { get; set; }

    public string? Parity { get; set; }

    public string? StopBits { get; set; }

    public string? Host { get; set; }

    public long? Port { get; set; }

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";
}
