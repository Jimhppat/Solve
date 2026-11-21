namespace Solve.Adapters.Persistence.Devices;

/// <summary>
/// bus_devices 表的一行，只使用 SQLite 原生类型（INTEGER → long，TEXT → string）。
/// 领域类型与数据库类型在 <see cref="DeviceRowMapper"/> 中显式转换，不依赖 Dapper 隐式类型处理。
/// </summary>
internal sealed class DeviceRow
{
    public string Id { get; set; } = "";

    public string BusId { get; set; } = "";

    public string DriverId { get; set; } = "";

    public long Address { get; set; }

    public string Name { get; set; } = "";

    public string? Remark { get; set; }

    /// <summary>
    /// 模块参数：JSON 对象（参数标识 → 文本值）
    /// </summary>
    public string Parameters { get; set; } = "{}";

    /// <summary>
    /// 通道配置：JSON 数组（见 DeviceRowMapper）
    /// </summary>
    public string Channels { get; set; } = "[]";

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";
}
