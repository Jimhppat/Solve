namespace Solve.Core.Domain.Topology;

/// <summary>
/// 设备数字孪生的六层结构：系统 → 柜体 → 层位 → 槽位 → 治具位 → 产品位。数值即深度（系统为 1）。
/// </summary>
/// <remarks>
/// 数值会被持久化 / 记入日志，已有成员的数值不得修改。
/// 从 1 开始编号，使 default 值成为非法值，未赋值会被校验拦截。
/// </remarks>
public enum TopologyLevel
{
    /// <summary>
    /// 系统（整台设备，树根，全局唯一）
    /// </summary>
    System = 1,

    /// <summary>
    /// 柜体
    /// </summary>
    Cabinet = 2,

    /// <summary>
    /// 层位（柜体内的一层）
    /// </summary>
    Layer = 3,

    /// <summary>
    /// 槽位（层位内的一个槽）
    /// </summary>
    Slot = 4,

    /// <summary>
    /// 治具位（槽位内放置治具的位置）
    /// </summary>
    Fixture = 5,

    /// <summary>
    /// 产品位（治具上放置单个产品的位置，叶子层）
    /// </summary>
    Product = 6,
}

public static class TopologyLevels
{
    /// <summary>
    /// 层数。产品位是叶子，不允许再有下级。
    /// </summary>
    public const int Count = 6;

    /// <summary>
    /// 自上而下的全部层级，供遍历与 UI 下拉使用
    /// </summary>
    public static IReadOnlyList<TopologyLevel> All { get; } =
    [
        TopologyLevel.System,
        TopologyLevel.Cabinet,
        TopologyLevel.Layer,
        TopologyLevel.Slot,
        TopologyLevel.Fixture,
        TopologyLevel.Product,
    ];

    /// <summary>
    /// 是否为已定义的层级。外部输入（配置、数据库）转换来的枚举值必须先检查。
    /// </summary>
    public static bool IsDefined(this TopologyLevel level)
        => level is >= TopologyLevel.System and <= TopologyLevel.Product;

    /// <summary>
    /// 下一层级。产品位没有下级，返回 false。
    /// </summary>
    public static bool TryGetChildLevel(this TopologyLevel level, out TopologyLevel child)
    {
        if (!level.IsDefined() || level == TopologyLevel.Product)
        {
            child = default;
            return false;
        }

        child = level + 1;
        return true;
    }

    /// <summary>
    /// 中文名，用于日志与界面
    /// </summary>
    public static string DisplayName(this TopologyLevel level) => level switch
    {
        TopologyLevel.System => "系统",
        TopologyLevel.Cabinet => "柜体",
        TopologyLevel.Layer => "层位",
        TopologyLevel.Slot => "槽位",
        TopologyLevel.Fixture => "治具位",
        TopologyLevel.Product => "产品位",
        _ => $"未定义层级({(int)level})",
    };

    /// <summary>
    /// 地址文本中该层的前缀字母（系统没有前缀，整体写作 SYS）。前缀属于地址格式，一经发布不得修改。
    /// </summary>
    internal static char AddressPrefix(this TopologyLevel level) => level switch
    {
        TopologyLevel.Cabinet => 'C',
        TopologyLevel.Layer => 'L',
        TopologyLevel.Slot => 'S',
        TopologyLevel.Fixture => 'F',
        TopologyLevel.Product => 'P',
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "只有柜体及以下层级有地址前缀。"),
    };
}
