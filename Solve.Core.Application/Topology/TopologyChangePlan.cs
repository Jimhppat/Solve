using Solve.Core.Domain.Topology;

namespace Solve.Core.Application.Topology;

/// <summary>
/// 一个位置编码的变化（节点 Id 不变）
/// </summary>
public readonly record struct TopologyAddressChange(TopologyAddress From, TopologyAddress To)
{
    public override string ToString() => $"{From} → {To}";
}

/// <summary>
/// 一次拓扑变更的预览：从当前生效的拓扑（<see cref="Base"/>）变为 <see cref="Candidate"/> 会新增、修改、删除哪些节点，
/// 哪些位置会被重新启用、停用或改变位置编码。节点按 Id 比对：位置编码变了但 Id 相同算"修改"，不算删除再新增。
/// </summary>
/// <remarks>
/// <para>以下变更必须先让操作员确认（<see cref="RequiresConfirmation"/>）：</para>
/// <list type="bullet">
/// <item>删除位置：引用它的历史数据在当前拓扑中失去落点，删除后无法恢复。</item>
/// <item>重新启用位置：解除维护屏蔽后联锁不再拦截发往这些位置的命令，需确认现场已具备运行条件。</item>
/// <item>新增并直接启用位置：同上，且新位置是否已在现场安装到位只有操作员知道（首次建档除外，此时设备尚未运行）。</item>
/// <item>改变位置编码：Id 不变、系统内部引用不受影响，但现场标牌与扫码标签若不同步更换，扫码会找到别的位置。</item>
/// </list>
/// <para>停用是故障安全方向（联锁会拦截命令），不需要确认，但会给出受影响的位置数。</para>
/// </remarks>
public sealed class TopologyChangePlan
{
    /// <summary>
    /// 提示中最多列出的位置数
    /// </summary>
    public const int MaxListedPositions = 10;

    private TopologyChangePlan(EquipmentTopology? @base, EquipmentTopology candidate, string? operation)
    {
        Base = @base;
        Candidate = candidate;
        Operation = operation;
    }

    /// <summary>
    /// 预览时生效的拓扑；首次建档为 null。保存时据此判断预览是否已过期。
    /// </summary>
    public EquipmentTopology? Base { get; }

    public EquipmentTopology Candidate { get; }

    /// <summary>
    /// 这次变更是什么操作（人读，含位置编码与节点 Id），保存时写入日志留痕；未提供为 null
    /// </summary>
    public string? Operation { get; }

    public int AddedCount { get; private init; }

    /// <summary>
    /// 新增且可用（自身与全部上级都启用）的节点数；首次建档不计
    /// </summary>
    public int AddedEnabledCount { get; private init; }

    public int AddedEnabledProductCount { get; private init; }

    /// <summary>
    /// 新增且可用的子树的最上层位置，按地址升序
    /// </summary>
    public IReadOnlyList<TopologyAddress> AddedEnabledRoots { get; private init; } = [];

    /// <summary>
    /// Id 不变、但位置编码 / 名称 / 启用状态 / 接点有变化的节点数
    /// </summary>
    public int ChangedCount { get; private init; }

    /// <summary>
    /// Id 不变、位置编码改变的节点数（改一个节点的序号，整棵子树的位置编码都会变）
    /// </summary>
    public int RenumberedCount { get; private init; }

    /// <summary>
    /// 位置编码改变的子树的最上层位置（原编码 → 新编码），按新编码升序
    /// </summary>
    public IReadOnlyList<TopologyAddressChange> RenumberedRoots { get; private init; } = [];

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

    public bool RequiresConfirmation => RemovedCount > 0 || ReenabledCount > 0 || AddedEnabledCount > 0 || RenumberedCount > 0;

    /// <param name="operation">操作说明，保存时写入日志</param>
    public static TopologyChangePlan Compare(EquipmentTopology? @base, EquipmentTopology candidate, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (@base is null)
            return new TopologyChangePlan(null, candidate, operation) { AddedCount = candidate.NodeCount };

        // 两边都按层自上而下遍历：上级总是先于下级判定，"上级也在同一集合里"可以直接查
        var added = 0;
        var changed = 0;
        var newlyDisabled = 0;
        var addedEnabled = new SubtreeTally(candidate);
        var renumbered = new SubtreeTally(candidate);
        var reenabled = new SubtreeTally(candidate);
        foreach (var node in TopologyLevels.All.SelectMany(candidate.GetNodes))
        {
            if (!@base.TryGetNode(node.Id, out var previous))
            {
                added++;
                if (node.IsEffectivelyEnabled)
                    addedEnabled.Add(node);
                continue;
            }

            if (previous.Address != node.Address)
                renumbered.Add(node);

            if (previous.Address != node.Address || previous.Name != node.Name || previous.IsEnabled != node.IsEnabled
                || !previous.Ports.SequenceEqual(node.Ports))
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

        return new TopologyChangePlan(@base, candidate, operation)
        {
            AddedCount = added,
            AddedEnabledCount = addedEnabled.Count,
            AddedEnabledProductCount = addedEnabled.ProductCount,
            AddedEnabledRoots = addedEnabled.SortedRoots(),
            ChangedCount = changed,
            RenumberedCount = renumbered.Count,
            RenumberedRoots = renumbered.SortedRootNodes()
                .Select(node => new TopologyAddressChange(@base.GetNode(node.Id).Value.Address, node.Address))
                .ToArray(),
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
    /// 统计一组节点（须自上而下加入）：总数、产品位数、以及上级不在组内的"最上层"节点
    /// </summary>
    private sealed class SubtreeTally(EquipmentTopology owner)
    {
        private readonly HashSet<TopologyNodeId> _ids = [];
        private readonly List<TopologyNode> _roots = [];

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
                _roots.Add(node);
        }

        public IReadOnlyList<TopologyNode> SortedRootNodes()
        {
            _roots.Sort(static (a, b) => a.Address.CompareTo(b.Address));
            return _roots;
        }

        public IReadOnlyList<TopologyAddress> SortedRoots() => SortedRootNodes().Select(node => node.Address).ToArray();
    }
}
