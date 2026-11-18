using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 统一布局模板：每个柜体的层数相同、每层的槽数相同……适用于标准化设备，按数量一次生成整棵树。
/// 柜体之间规格不同时，直接构造 <see cref="TopologyDefinition"/>。
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
    /// 生成结构定义，序号从 1 连续编号。布局无效时返回 Topology.InvalidLayout。
    /// </summary>
    /// <remarks>
    /// 同一层的下级定义完全相同，整层共享同一份不可变列表：生成开销只与各层数量之和相关，与总节点数无关。
    /// 系统名称由 <see cref="EquipmentTopology.Create(TopologyDefinition)"/> 统一校验。
    /// </remarks>
    public Result<TopologyDefinition> ToDefinition(string systemName)
    {
        var errors = Validate();
        if (errors.Count > 0)
            return Result<TopologyDefinition>.Failure(TopologyErrors.InvalidLayout(errors));

        // 自底向上：先生成产品位列表，再逐层包一层上级
        IReadOnlyList<TopologyNodeDefinition> level = [];
        foreach (var count in Counts().Reverse())
        {
            var shared = level;
            level = Enumerable.Range(1, count).Select(ordinal => new TopologyNodeDefinition(ordinal, shared)).ToArray();
        }

        return Result<TopologyDefinition>.Success(new TopologyDefinition(systemName, level));
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
