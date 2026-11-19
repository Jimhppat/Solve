namespace Solve.Core.Domain.Twin;

/// <summary>
/// 产品位的状态（数字孪生显示用）：空闲、待机、良品、不良品。
/// </summary>
/// <remarks>
/// <para>default 为 <see cref="Idle"/>：产品位在收到第一次上报之前显示为空闲。
/// 暂不做通信超时判断（状态机尚未实现）：没有新的上报时保持最后一次的状态，界面上没有"数据过期"的提示，
/// 通信是否正常要看链路状态。</para>
/// <para>数值会记入日志、将来可能持久化：确定后已有成员的数值不得修改，新增成员只能追加。</para>
/// <para>"停用（维护屏蔽）"不是产品状态，取自拓扑节点的启用设置，单独计数。</para>
/// </remarks>
public enum ProductState
{
    /// <summary>
    /// 空闲：默认状态
    /// </summary>
    Idle = 0,

    /// <summary>
    /// 待机
    /// </summary>
    Standby = 1,

    /// <summary>
    /// 良品（合格）
    /// </summary>
    Good = 2,

    /// <summary>
    /// 不良品（不合格）
    /// </summary>
    Defective = 3,
}

public static class ProductStates
{
    public const int Count = 4;

    /// <summary>
    /// 按数值顺序（空闲、待机、良品、不良品）：图例用这个顺序
    /// </summary>
    public static IReadOnlyList<ProductState> All { get; } =
    [
        ProductState.Idle,
        ProductState.Standby,
        ProductState.Good,
        ProductState.Defective,
    ];

    /// <summary>
    /// 按严重度从高到低排列：汇总文字、比例条分段都按这个顺序
    /// </summary>
    public static IReadOnlyList<ProductState> BySeverity { get; } =
    [
        ProductState.Defective,
        ProductState.Standby,
        ProductState.Good,
        ProductState.Idle,
    ];

    /// <summary>
    /// 外部输入（数据源映射、配置）转换来的枚举值必须先检查
    /// </summary>
    public static bool IsDefined(this ProductState state) => state is >= ProductState.Idle and <= ProductState.Defective;

    public static string DisplayName(this ProductState state) => state switch
    {
        ProductState.Idle => "空闲",
        ProductState.Standby => "待机",
        ProductState.Good => "良品",
        ProductState.Defective => "不良品",
        _ => $"未定义状态({(int)state})",
    };

    /// <summary>
    /// 严重度，越大越需要关注。容器的边框与圆点取其下最严重的状态：不良品 &gt; 待机 &gt; 良品 &gt; 空闲。
    /// 待机排在良品前面：待机的产品还在等人或设备处理，良品已经正常结束。
    /// </summary>
    public static int Severity(this ProductState state) => state switch
    {
        ProductState.Defective => 3,
        ProductState.Standby => 2,
        ProductState.Good => 1,
        ProductState.Idle => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未定义的产品状态。"),
    };

    /// <summary>
    /// 告警类状态：只有不良品，容器上用红字、柜体切换胶囊上用红色数字单独计数。
    /// </summary>
    public static bool IsAlarm(this ProductState state) => state is ProductState.Defective;
}
