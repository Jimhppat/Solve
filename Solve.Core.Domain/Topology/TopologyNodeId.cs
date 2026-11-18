using System.Diagnostics.CodeAnalysis;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 拓扑节点的系统身份（机器用）。六层中的每个位置都有一个，创建时生成一次、持久化后终身不变。
/// </summary>
/// <remarks>
/// <para>双身份约定：系统内部的一切引用（总线映射、测试记录、告警、历史数据、MES 接口）只认本 Id；
/// 人看的 <see cref="TopologyAddress"/>（位置编码）与显示名只用于界面、扫码、录入和日志阅读，可以随重新编号而改变。
/// 这样柜体重新编号或布局调整后，历史数据仍然挂在同一个节点上。</para>
/// <para>禁止从地址推导 Id（如对地址做 UUIDv5 散列）：那样重新编号时 Id 会跟着变，等于又把身份绑回了编号。</para>
/// <para>default(TopologyNodeId) 为空值，定义校验时拒绝。</para>
/// </remarks>
public readonly record struct TopologyNodeId(Guid Value)
{
    public static readonly TopologyNodeId Empty = default;

    /// <summary>
    /// 生成 UUIDv7 节点标识（按创建时间有序，作为数据库主键时索引局部性好）。
    /// 只在首次建档时调用；已有节点必须沿用持久化的 Id。
    /// </summary>
    public static TopologyNodeId New() => new(Guid.CreateVersion7());

    /// <summary>
    /// 从字符串解析，格式非法时抛出 <see cref="FormatException"/>，仅用于可信输入
    /// </summary>
    public static TopologyNodeId Parse(string value) => new(Guid.Parse(value));

    /// <summary>
    /// 从字符串解析，外部输入请使用此方法；空 GUID 视为解析失败
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out TopologyNodeId id)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            id = new TopologyNodeId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D");
}
