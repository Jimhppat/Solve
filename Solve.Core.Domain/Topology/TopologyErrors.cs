using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 拓扑错误码。Code 一经发布即视为稳定接口（日志检索、告警映射、UI 字段高亮依赖它）。
/// </summary>
public static class TopologyErrors
{
    // ---------- 结构定义（TopologyDefinition）----------

    public const string InvalidDefinitionCode = "Topology.InvalidDefinition";

    /// <summary>
    /// 定义字段错误的 Code 前缀，完整 Code 形如 Topology.Definition.Ordinal
    /// </summary>
    public const string DefinitionCodePrefix = "Topology.Definition.";

    public const string NameField = "Name";
    public const string OrdinalField = "Ordinal";
    public const string DuplicateOrdinalField = "DuplicateOrdinal";
    public const string ChildrenField = "Children";
    public const string DepthField = "Depth";
    public const string NodeLimitField = "NodeLimit";

    public static Error InvalidField(string field, string message) => new(DefinitionCodePrefix + field, message);

    /// <param name="errors">已收集的错误（可能被截断）</param>
    /// <param name="totalCount">错误总数，大于 errors.Count 时说明有错误未列出</param>
    public static Error InvalidDefinition(IReadOnlyList<Error> errors, int totalCount)
    {
        var detail = string.Join("；", errors.Select(e => e.ToString()));
        if (totalCount > errors.Count)
            detail += $"；……共 {totalCount} 处错误，仅列出前 {errors.Count} 处";

        return new Error(InvalidDefinitionCode, "拓扑结构定义无效：" + detail);
    }

    // ---------- 统一布局模板（TopologyLayout）----------

    public const string InvalidLayoutCode = "Topology.InvalidLayout";

    /// <summary>
    /// 布局字段错误的 Code 前缀，完整 Code 形如 Topology.Layout.SlotsPerLayer
    /// </summary>
    public const string LayoutCodePrefix = "Topology.Layout.";

    public static Error InvalidLayoutField(string field, string message) => new(LayoutCodePrefix + field, message);

    public static Error InvalidLayout(IReadOnlyList<Error> errors)
        => new(InvalidLayoutCode, "拓扑布局无效：" + string.Join("；", errors.Select(e => e.ToString())));

    // ---------- 查询与联锁 ----------

    public const string NodeNotFoundCode = "Topology.NodeNotFound";
    public const string NodeDisabledCode = "Topology.NodeDisabled";

    public static Error NodeNotFound(TopologyAddress address)
        => new(NodeNotFoundCode, $"拓扑中不存在位置 {address}。");

    /// <param name="target">请求操作的位置</param>
    /// <param name="disabledAt">自上而下第一个被停用的节点（可能是目标自身，也可能是它的祖先）</param>
    public static Error NodeDisabled(TopologyAddress target, TopologyAddress disabledAt)
        => new(NodeDisabledCode, target == disabledAt
            ? $"位置 {target} 已停用。"
            : $"位置 {target} 所属的{disabledAt.Level.DisplayName()} {disabledAt} 已停用。");
}
