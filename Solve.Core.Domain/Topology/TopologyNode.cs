using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 已校验的拓扑节点（不可变）。只能由 <see cref="EquipmentTopology"/> 创建，可被任意线程无锁并发读取。
/// </summary>
public sealed class TopologyNode
{
    // 按序号升序存放：TryGetChild 二分查找，且先序遍历结果即地址顺序
    private readonly TopologyNode[] _children;

    internal TopologyNode(
        TopologyNodeId id,
        TopologyAddress address,
        string? name,
        bool isEnabled,
        bool isEffectivelyEnabled,
        IReadOnlyList<PortGroup> ports,
        IReadOnlyList<RequiredPort> requiredPorts,
        TopologyNode[] children)
    {
        Id = id;
        Address = address;
        Name = name;
        IsEnabled = isEnabled;
        IsEffectivelyEnabled = isEffectivelyEnabled;
        Ports = ports;
        RequiredPorts = requiredPorts;
        _children = children;
        Children = children.Length == 0 ? ReadOnlyCollection<TopologyNode>.Empty : Array.AsReadOnly(children);
    }

    /// <summary>
    /// 系统身份（机器用）：系统内部引用、存储、接口只认它，终身不变
    /// </summary>
    public TopologyNodeId Id { get; }

    /// <summary>
    /// 位置编码（人用）：全树唯一，用于界面、扫码、录入；重新编号后会改变，不要作为外键保存
    /// </summary>
    public TopologyAddress Address { get; }

    public TopologyLevel Level => Address.Level;

    /// <summary>
    /// 在上级中的序号；系统根为 0
    /// </summary>
    public int Ordinal => Address.Ordinal;

    /// <summary>
    /// 配置的显示名，未配置为 null
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// 界面显示名：有配置名用配置名，否则用地址
    /// </summary>
    public string DisplayName => Name ?? Address.ToString();

    /// <summary>
    /// 本节点自身是否启用（配置值）
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// 本节点及全部祖先是否都启用。任一祖先停用，整棵子树都视为停用。
    /// </summary>
    public bool IsEffectivelyEnabled { get; }

    /// <summary>
    /// 本位置自身声明的接点组（配置值，已规范化）
    /// </summary>
    public IReadOnlyList<PortGroup> Ports { get; }

    /// <summary>
    /// 本位置下的产品位要求的接点：全部上级与自身声明的接点组展开后合并（上级的在前；同一接点只保留最上层的声明）。
    /// 只在产品位上核对，见 <see cref="PortGroup"/>
    /// </summary>
    public IReadOnlyList<RequiredPort> RequiredPorts { get; }

    /// <summary>
    /// 下级节点，按序号升序
    /// </summary>
    public IReadOnlyList<TopologyNode> Children { get; }

    /// <summary>
    /// 新增下级时使用的第一个序号：现有最大序号 + 1，没有下级时为 1。
    /// 不回填中间的空号：缺位通常是现场有意留空（如拆走的槽位），回填会让新位置顶替旧编号。
    /// 结果可能超过 <see cref="TopologyAddress.MaxOrdinal"/>，表示序号已用尽。
    /// </summary>
    public int NextChildOrdinal => _children.Length == 0 ? 1 : _children[^1].Ordinal + 1;

    public bool TryGetChild(int ordinal, [NotNullWhen(true)] out TopologyNode? child)
    {
        var low = 0;
        var high = _children.Length - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var current = _children[mid].Ordinal;
            if (current == ordinal)
            {
                child = _children[mid];
                return true;
            }

            if (current < ordinal)
                low = mid + 1;
            else
                high = mid - 1;
        }

        child = null;
        return false;
    }

    /// <summary>
    /// 日志格式：人读的层级、位置编码、显示名在前，机器检索用的 Id 在后，两套身份同时留痕
    /// </summary>
    public override string ToString()
        => Name is null
            ? $"{Level.DisplayName()} {Address} [{Id}]"
            : $"{Level.DisplayName()} {Address}（{Name}）[{Id}]";
}
