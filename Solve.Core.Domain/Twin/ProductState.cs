namespace Solve.Core.Domain.Twin;

/// <summary>
/// 产品位的运行状态（数字孪生显示用）。
/// </summary>
/// <remarks>
/// <para>数值会记入日志、将来可能持久化，已有成员的数值不得修改，新增成员只能追加。</para>
/// <para>default 为 <see cref="Unknown"/>：没收到数据、通信中断或数据过期时一律显示"无数据"，
/// 绝不默认成任何表示"正常"的状态（故障安全）。</para>
/// <para>"停用（维护屏蔽）"不是产品状态，取自拓扑节点的启用设置，单独计数。</para>
/// </remarks>
public enum ProductState
{
    /// <summary>
    /// 无数据：未收到、通信中断或超时未更新
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// 空位：没有产品
    /// </summary>
    Empty = 1,

    /// <summary>
    /// 待测：已上料，尚未开始
    /// </summary>
    Ready = 2,

    /// <summary>
    /// 测试中：老化 / 测试进行中
    /// </summary>
    Running = 3,

    /// <summary>
    /// 合格：测试结束且合格
    /// </summary>
    Passed = 4,

    /// <summary>
    /// 不合格：测试结束但不合格
    /// </summary>
    Failed = 5,

    /// <summary>
    /// 报警：参数超限，但仍在测试
    /// </summary>
    Alarm = 6,

    /// <summary>
    /// 故障：通道或位置故障，需要人工处理
    /// </summary>
    Fault = 7,
}

public static class ProductStates
{
    public const int Count = 8;

    /// <summary>
    /// 按严重度从高到低排列：汇总显示、比例条分段都按这个顺序
    /// </summary>
    public static IReadOnlyList<ProductState> BySeverity { get; } =
    [
        ProductState.Fault,
        ProductState.Alarm,
        ProductState.Failed,
        ProductState.Unknown,
        ProductState.Running,
        ProductState.Ready,
        ProductState.Passed,
        ProductState.Empty,
    ];

    /// <summary>
    /// 外部输入（数据源映射、配置）转换来的枚举值必须先检查
    /// </summary>
    public static bool IsDefined(this ProductState state) => state is >= ProductState.Unknown and <= ProductState.Fault;

    public static string DisplayName(this ProductState state) => state switch
    {
        ProductState.Unknown => "无数据",
        ProductState.Empty => "空位",
        ProductState.Ready => "待测",
        ProductState.Running => "测试中",
        ProductState.Passed => "合格",
        ProductState.Failed => "不合格",
        ProductState.Alarm => "报警",
        ProductState.Fault => "故障",
        _ => $"未定义状态({(int)state})",
    };

    /// <summary>
    /// 严重度，越大越需要关注。容器的颜色取其下最严重的状态：故障 &gt; 报警 &gt; 不合格 &gt; 无数据 &gt; 测试中 &gt; 待测 &gt; 合格 &gt; 空位。
    /// </summary>
    public static int Severity(this ProductState state) => state switch
    {
        ProductState.Fault => 7,
        ProductState.Alarm => 6,
        ProductState.Failed => 5,
        ProductState.Unknown => 4,
        ProductState.Running => 3,
        ProductState.Ready => 2,
        ProductState.Passed => 1,
        ProductState.Empty => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未定义的产品状态。"),
    };

    /// <summary>
    /// 需要操作员关注：故障、报警、不合格、无数据
    /// </summary>
    public static bool NeedsAttention(this ProductState state) => state.Severity() >= ProductState.Unknown.Severity();
}
