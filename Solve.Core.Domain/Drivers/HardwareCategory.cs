namespace Solve.Core.Domain.Drivers;

/// <summary>
/// 驱动所属的硬件分类，供模块配置窗口按类筛选驱动。分类只存在于代码中的驱动目录，不写入数据库。
/// </summary>
/// <remarks>
/// 从 1 开始编号，使 default 值成为非法值。新增分类只能追加，界面按 <see cref="HardwareCategories.All"/> 的顺序显示。
/// </remarks>
public enum HardwareCategory
{
    /// <summary>
    /// 通用（不含协议的通用模块等）
    /// </summary>
    General = 1,

    /// <summary>
    /// 电源 / 电子负载
    /// </summary>
    PowerAndLoad = 2,
}

public static class HardwareCategories
{
    /// <summary>
    /// 界面显示顺序
    /// </summary>
    public static IReadOnlyList<HardwareCategory> All { get; } =
    [
        HardwareCategory.PowerAndLoad,
        HardwareCategory.General,
    ];

    public static string DisplayName(this HardwareCategory category) => category switch
    {
        HardwareCategory.General => "通用",
        HardwareCategory.PowerAndLoad => "电源 / 电子负载",
        _ => $"未定义分类({(int)category})",
    };
}
