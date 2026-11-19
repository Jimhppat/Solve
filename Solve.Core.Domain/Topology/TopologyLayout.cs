using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 统一布局模板：每个柜体的层数相同、每层的槽数相同……按数量一次生成整棵树，用于首次建档。
/// 建档后各节点的下级可以各不相同，用 <see cref="EquipmentTopology"/> 的结构编辑方法逐个增删改。
/// </summary>
/// <param name="Cabinets">柜体数</param>
/// <param name="LayersPerCabinet">每柜层位数</param>
/// <param name="SlotsPerLayer">每层槽位数</param>
/// <param name="FixturesPerSlot">每槽治具位数</param>
/// <param name="ProductsPerFixture">每治具产品位数</param>
public sealed record TopologyLayout(
    int Cabinets,
    int LayersPerCabinet,
    int SlotsPerLayer,
    int FixturesPerSlot,
    int ProductsPerFixture)
{
    /// <summary>
    /// 整棵树的节点总数（含系统根）。
    /// 用 long 计算并把每层数量钳到 [0, MaxOrdinal]：各层都是 999 时连乘约 1e15，int 会溢出成负数而绕过上限检查。
    /// </summary>
    public long TotalNodeCount
    {
        get
        {
            long total = 1;
            long levelCount = 1;
            foreach (var count in Counts())
            {
                levelCount *= Math.Clamp(count, 0, TopologyAddress.MaxOrdinal);
                total += levelCount;
            }

            return total;
        }
    }

    /// <summary>
    /// 产品位总数
    /// </summary>
    public long ProductCount
        => Counts().Aggregate(1L, (product, count) => product * Math.Clamp(count, 0, TopologyAddress.MaxOrdinal));

    /// <summary>
    /// 校验全部字段并一次性返回所有错误。空列表表示有效。
    /// </summary>
    public IReadOnlyList<Error> Validate()
    {
        var errors = new List<Error>();
        CheckCount(errors, nameof(Cabinets), Cabinets, TopologyLevel.Cabinet);
        CheckCount(errors, nameof(LayersPerCabinet), LayersPerCabinet, TopologyLevel.Layer);
        CheckCount(errors, nameof(SlotsPerLayer), SlotsPerLayer, TopologyLevel.Slot);
        CheckCount(errors, nameof(FixturesPerSlot), FixturesPerSlot, TopologyLevel.Fixture);
        CheckCount(errors, nameof(ProductsPerFixture), ProductsPerFixture, TopologyLevel.Product);

        if (errors.Count == 0 && TotalNodeCount > EquipmentTopology.MaxNodeCount)
        {
            errors.Add(TopologyErrors.InvalidLayoutField("NodeCount",
                $"节点总数 {TotalNodeCount} 超过上限 {EquipmentTopology.MaxNodeCount}。"));
        }

        return errors;
    }

    /// <summary>
    /// 按布局生成结构定义，序号从 1 连续编号。布局无效时返回 Topology.InvalidLayout。
    /// </summary>
    /// <param name="systemName">系统名称，由 <see cref="EquipmentTopology.Create(TopologyDefinition)"/> 统一校验</param>
    /// <param name="current">
    /// 现有拓扑。为 null 表示首次建档，全部节点生成新 Id；
    /// 否则为扩容 / 缩容：同一位置编码的节点沿用原 Id、名称与启用状态（维护屏蔽不会因改布局被清掉），
    /// 新增位置生成新 Id，被移除位置的节点不再出现（历史数据仍可按原 Id 追溯）。
    /// </param>
    public Result<TopologyDefinition> ToDefinition(string systemName, EquipmentTopology? current = null)
    {
        var errors = Validate();
        if (errors.Count > 0)
            return Result<TopologyDefinition>.Failure(TopologyErrors.InvalidLayout(errors));

        var cabinets = BuildLevel(TopologyAddress.System, Counts().ToArray(), depth: 0, current);
        var rootId = current?.Root.Id ?? TopologyNodeId.New();
        return Result<TopologyDefinition>.Success(new TopologyDefinition(rootId, systemName, cabinets));
    }

    /// <summary>
    /// 每个节点独立生成定义：Id 必须全树唯一，不能像纯结构那样整层共享子列表。
    /// 也供 <see cref="EquipmentTopology.WithChildLayout"/> 以任意节点为起点重建下级。
    /// </summary>
    internal static TopologyNodeDefinition[] BuildLevel(
        TopologyAddress parent,
        int[] counts,
        int depth,
        EquipmentTopology? current)
    {
        if (depth == counts.Length)
            return [];

        var nodes = new TopologyNodeDefinition[counts[depth]];
        for (var i = 0; i < nodes.Length; i++)
        {
            var ordinal = i + 1;
            var address = parent.Child(ordinal);
            var children = BuildLevel(address, counts, depth + 1, current);

            nodes[i] = current is not null && current.TryGetNode(address, out var existing)
                ? new TopologyNodeDefinition(existing.Id, ordinal, children) { Name = existing.Name, Enabled = existing.IsEnabled }
                : new TopologyNodeDefinition(TopologyNodeId.New(), ordinal, children);
        }

        return nodes;
    }

    // 自上而下：柜体、层位、槽位、治具位、产品位
    private IEnumerable<int> Counts() => [Cabinets, LayersPerCabinet, SlotsPerLayer, FixturesPerSlot, ProductsPerFixture];

    private static void CheckCount(List<Error> errors, string field, int value, TopologyLevel level)
    {
        if (value is < 1 or > TopologyAddress.MaxOrdinal)
        {
            errors.Add(TopologyErrors.InvalidLayoutField(field,
                $"{level.DisplayName()}数量必须在 [1, {TopologyAddress.MaxOrdinal}] 之间，当前为 {value}。"));
        }
    }
}
