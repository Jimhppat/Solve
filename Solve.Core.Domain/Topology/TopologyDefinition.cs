namespace Solve.Core.Domain.Topology;

/// <summary>
/// 整台设备的拓扑结构定义（系统 → 柜体 → …… → 产品位）。
/// </summary>
/// <remarks>
/// 定义来自数据库、配置文件或 UI 输入，属于不可信数据：只能通过 <see cref="EquipmentTopology.Create(TopologyDefinition)"/>
/// 校验后使用。柜体之间规格不同（异构）时直接构造本类型；全部规格一致时用 <see cref="TopologyLayout"/> 生成。
/// </remarks>
/// <param name="Name">系统名称（必填）</param>
/// <param name="Cabinets">柜体定义</param>
public sealed record TopologyDefinition(string Name, IReadOnlyList<TopologyNodeDefinition> Cabinets);

/// <summary>
/// 柜体及以下任意一层的节点定义。所属层级由它在树中的深度决定，不单独声明，
/// 避免出现"声明层级与实际深度不一致"这类冗余错误。
/// </summary>
/// <param name="Ordinal">在上级中的序号，从 1 开始，同级内唯一，允许不连续（现场缺位）</param>
/// <param name="Children">下级定义；产品位必须为空列表</param>
public sealed record TopologyNodeDefinition(int Ordinal, IReadOnlyList<TopologyNodeDefinition> Children)
{
    /// <summary>
    /// 显示名，如 "A 柜"、"高温层"。为空时以地址作为显示名。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 是否启用。停用（维护屏蔽）的节点连同全部下级都视为不可用，联锁检查会拒绝向其下发命令。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 没有下级的节点（产品位）
    /// </summary>
    public static TopologyNodeDefinition Leaf(int ordinal) => new(ordinal, []);
}
