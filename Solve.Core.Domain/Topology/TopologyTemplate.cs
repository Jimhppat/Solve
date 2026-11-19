using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Topology;

/// <summary>
/// 新增节点时一并生成的下级结构：从新节点的下一层起，每层每个节点有几个下级。
/// 如在柜体下新增层位、模板为 [4, 1, 2]：每个新层位下 4 个槽位、每槽 1 个治具位、每治具 2 个产品位。
/// </summary>
/// <remarks>
/// 模板可以比剩余层数短，生成到模板的最后一层为止（<see cref="Empty"/> 只新增节点本身）。
/// 生成的下级全部分配新 Id、启用、未命名，序号从 1 连续编号。
/// </remarks>
public sealed class TopologyTemplate
{
    private readonly int[] _counts;

    /// <param name="countsBelow">自新节点的下一层起，每层每个节点的下级数</param>
    public TopologyTemplate(IEnumerable<int> countsBelow)
    {
        ArgumentNullException.ThrowIfNull(countsBelow);

        // 防御性复制：调用方之后改动原数组不能影响已校验的模板
        _counts = countsBelow.ToArray();
        CountsBelow = Array.AsReadOnly(_counts);
    }

    /// <summary>
    /// 只新增节点本身，不带下级
    /// </summary>
    public static TopologyTemplate Empty { get; } = new([]);

    public IReadOnlyList<int> CountsBelow { get; }

    /// <summary>
    /// 一个新节点连同全部下级的节点数。
    /// 用 long 计算并把每层数量钳到 [0, MaxOrdinal]：各层都是 999 时连乘约 1e12，int 会溢出成负数而绕过上限检查。
    /// </summary>
    public long NodeCountPerInstance
    {
        get
        {
            long total = 1;
            long levelCount = 1;
            foreach (var count in _counts)
            {
                levelCount *= Clamp(count);
                total += levelCount;
            }

            return total;
        }
    }

    /// <summary>
    /// 一个 <paramref name="instanceLevel"/> 层的新节点下有几个产品位：模板一直生成到产品位才有；
    /// 新节点本身就是产品位时为 1。
    /// </summary>
    public long ProductCountPerInstance(TopologyLevel instanceLevel)
        => (int)instanceLevel + _counts.Length == (int)TopologyLevel.Product
            ? _counts.Aggregate(1L, static (product, count) => product * Clamp(count))
            : 0;

    /// <summary>
    /// 校验模板能否用于新增 <paramref name="instanceLevel"/> 层的节点：层数不超过其下剩余的层数，每层数量在 [1, MaxOrdinal] 之间
    /// </summary>
    public Result Validate(TopologyLevel instanceLevel)
    {
        if (!instanceLevel.IsDefined() || instanceLevel == TopologyLevel.System)
            return Result.Failure(TopologyErrors.InvalidEdit($"不能新增{instanceLevel.DisplayName()}节点。"));

        var levelsBelow = (int)TopologyLevel.Product - (int)instanceLevel;
        if (_counts.Length > levelsBelow)
        {
            return Result.Failure(TopologyErrors.InvalidEdit(
                $"{instanceLevel.DisplayName()}下面只有 {levelsBelow} 层，下级模板却有 {_counts.Length} 层。"));
        }

        for (var i = 0; i < _counts.Length; i++)
        {
            if (_counts[i] is < 1 or > TopologyAddress.MaxOrdinal)
            {
                var level = (TopologyLevel)((int)instanceLevel + i + 1);
                return Result.Failure(TopologyErrors.InvalidEdit(
                    $"下级模板中的{level.DisplayName()}数量必须在 [1, {TopologyAddress.MaxOrdinal}] 之间，当前为 {_counts[i]}。"));
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// 在 <paramref name="parent"/> 下生成 <paramref name="count"/> 个新节点（序号从 <paramref name="firstOrdinal"/> 起连续），
    /// 每个都按模板带上下级。调用方须先 <see cref="Validate"/> 并确认序号与节点总数都不越界。
    /// </summary>
    /// <param name="enabled">新节点自身的启用状态；下级都是启用，由新节点统一屏蔽</param>
    internal TopologyNodeDefinition[] Build(TopologyAddress parent, int firstOrdinal, int count, bool enabled)
    {
        var nodes = new TopologyNodeDefinition[count];
        for (var i = 0; i < count; i++)
        {
            var ordinal = firstOrdinal + i;
            var children = TopologyLayout.BuildLevel(parent.Child(ordinal), _counts, depth: 0, current: null);
            nodes[i] = new TopologyNodeDefinition(TopologyNodeId.New(), ordinal, children) { Enabled = enabled };
        }

        return nodes;
    }

    private static long Clamp(int count) => Math.Clamp(count, 0, TopologyAddress.MaxOrdinal);
}
