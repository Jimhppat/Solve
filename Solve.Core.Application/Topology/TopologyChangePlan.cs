using Solve.Core.Domain.Topology;

namespace Solve.Core.Application.Topology;

/// <summary>
/// 一次拓扑变更的预览：从当前生效的拓扑（<see cref="Base"/>）变为 <see cref="Candidate"/> 会新增、修改、删除哪些节点，
/// 哪些位置会被重新启用或停用。节点按 Id 比对：位置编码变了但 Id 相同算"修改"，不算删除再新增。
/// </summary>
/// <remarks>
/// <para>两类变更必须先让操作员确认（<see cref="RequiresConfirmation"/>）：</para>
/// <list type="bullet">
/// <item>删除位置：引用它的历史数据在当前拓扑中失去落点，删除后无法恢复。</item>
/// <item>重新启用位置：解除维护屏蔽后联锁不再拦截发往这些位置的命令，需确认现场已具备运行条件。</item>
/// </list>
/// <para>停用是故障安全方向（联锁会拦截命令），不需要确认，但会给出受影响的位置数。</para>
/// </remarks>
public sealed class TopologyChangePlan
{
    /// <summary>
    /// 提示中最多列出的位置数
    /// </summary>
    public const int MaxListedPositions = 10;

    private TopologyChangePlan(EquipmentTopology? @base, EquipmentTopology candidate)
    {
        Base = @base;
        Candidate = candidate;
    }

    /// <summary>
    /// 预览时生效的拓扑；首次建档为 null。保存时据此判断预览是否已过期。
    /// </summary>
    public EquipmentTopology? Base { get; }

    public EquipmentTopology Candidate { get; }

    public int AddedCount { get; private init; }

    /// <summary>
    /// Id 不变、但位置编码 / 名称 / 启用状态有变化的节点数
    /// </summary>
    public int ChangedCount { get; private init; }

    public int RemovedCount { get; private init; }

    /// <summary>
    /// 被删除的节点中产品位的个数
    /// </summary>
    public int RemovedProductCount { get; private init; }

    /// <summary>
    /// 被删除子树的最上层位置（删整个 3 号柜只列 C03，不逐个列出其下级），按地址升序
    /// </summary>
    public IReadOnlyList<TopologyAddress> RemovedRoots { get; private init; } = [];

    /// <summary>
    /// 原先处于停用（自身或上级被停用）、变更后重新可用的节点数
    /// </summary>
    public int ReenabledCount { get; private init; }

    public int ReenabledProductCount { get; private init; }

    /// <summary>
    /// 重新启用的子树的最上层位置（按变更后的地址），按地址升序
    /// </summary>
    public IReadOnlyList<TopologyAddress> ReenabledRoots { get; private init; } = [];

    /// <summary>
    /// 原先可用、变更后被停用（自身或上级被停用）的节点数
    /// </summary>
    public int NewlyDisabledCount { get; private init; }

    public bool IsFirstCreation => Base is null;

    public bool HasChanges => IsFirstCreation || AddedCount + ChangedCount + RemovedCount > 0;

    public bool RequiresConfirmation => RemovedCount > 0 || ReenabledCount > 0;

    public static TopologyChangePlan Compare(EquipmentTopology? @base, EquipmentTopology candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (@base is null)
            return new TopologyChangePlan(null, candidate) { AddedCount = candidate.NodeCount };

        // 两边都按层自上而下遍历：上级总是先于下级判定，"上级也在同一集合里"可以直接查
        var added = 0;
        var changed = 0;
        var newlyDisabled = 0;
        var reenabled = new SubtreeTally(candidate);
        foreach (var node in TopologyLevels.All.SelectMany(candidate.GetNodes))
        {
            if (!@base.TryGetNode(node.Id, out var previous))
            {
                added++;
                continue;
            }

            if (previous.Address != node.Address || previous.Name != node.Name || previous.IsEnabled != node.IsEnabled)
                changed++;

            if (!previous.IsEffectivelyEnabled && node.IsEffectivelyEnabled)
                reenabled.Add(node);
            else if (previous.IsEffectivelyEnabled && !node.IsEffectivelyEnabled)
                newlyDisabled++;
        }

        var removed = new SubtreeTally(@base);
        foreach (var node in TopologyLevels.All.SelectMany(@base.GetNodes))
        {
            if (!candidate.TryGetNode(node.Id, out _))
                removed.Add(node);
        }

        return new TopologyChangePlan(@base, candidate)
        {
            AddedCount = added,
            ChangedCount = changed,
            NewlyDisabledCount = newlyDisabled,
            RemovedCount = removed.Count,
            RemovedProductCount = removed.ProductCount,
            RemovedRoots = removed.SortedRoots(),
            ReenabledCount = reenabled.Count,
            ReenabledProductCount = reenabled.ProductCount,
            ReenabledRoots = reenabled.SortedRoots(),
        };
    }

    /// <summary>
    /// 统计一组节点（须自上而下加入）：总数、产品位数、以及上级不在组内的"最上层"位置
    /// </summary>
    private sealed class SubtreeTally(EquipmentTopology owner)
    {
        private readonly HashSet<TopologyNodeId> _ids = [];
        private readonly List<TopologyAddress> _roots = [];

        public int Count => _ids.Count;

        public int ProductCount { get; private set; }

        public void Add(TopologyNode node)
        {
            _ids.Add(node.Id);
            if (node.Level == TopologyLevel.Product)
                ProductCount++;

            var parentInSet = node.Address.TryGetParent(out var parentAddress)
                && owner.TryGetNode(parentAddress, out var parent)
                && _ids.Contains(parent.Id);
            if (!parentInSet)
                _roots.Add(node.Address);
        }

        public IReadOnlyList<TopologyAddress> SortedRoots()
        {
            _roots.Sort();
            return _roots;
        }
    }
}
