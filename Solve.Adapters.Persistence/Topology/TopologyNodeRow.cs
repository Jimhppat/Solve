namespace Solve.Adapters.Persistence.Topology;

/// <summary>
/// topology_nodes 表的一行，只使用 SQLite 原生类型（INTEGER → long，TEXT → string）。
/// 领域类型与数据库类型在 <see cref="TopologyRowMapper"/> 中显式转换，不依赖 Dapper 隐式类型处理。
/// </summary>
internal sealed class TopologyNodeRow
{
    public string Id { get; set; } = "";

    /// <summary>
    /// 上级节点 Id；只有系统根为 NULL
    /// </summary>
    public string? ParentId { get; set; }

    public long Level { get; set; }

    /// <summary>
    /// 在上级中的序号；系统根为 0
    /// </summary>
    public long Ordinal { get; set; }

    /// <summary>
    /// 人用位置编码（冗余存储，加载时核对）
    /// </summary>
    public string Address { get; set; } = "";

    /// <summary>
    /// 显示名；系统根存系统名称
    /// </summary>
    public string? Name { get; set; }

    public long Enabled { get; set; }

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";

    /// <summary>
    /// 业务内容是否相同（不比较时间戳），决定保存时是否刷新 updated_at
    /// </summary>
    public bool HasSameContentAs(TopologyNodeRow other)
        => string.Equals(ParentId, other.ParentId, StringComparison.Ordinal)
            && Level == other.Level
            && Ordinal == other.Ordinal
            && string.Equals(Address, other.Address, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && Enabled == other.Enabled;
}
