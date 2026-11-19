namespace Solve.Device.Sdk;

/// <summary>
/// 设备分类，决定驱动出现在模块配置窗口的哪个分类下。数值写入契约，已有成员不得修改，新增只能追加。
/// </summary>
public enum DeviceCategory
{
    /// <summary>
    /// 通用
    /// </summary>
    General = 1,

    /// <summary>
    /// 电源 / 电子负载
    /// </summary>
    PowerAndLoad = 2,
}

/// <summary>
/// 点位的读写权限
/// </summary>
public enum PointAccess
{
    Read = 1,
    ReadWrite = 2,
}

/// <summary>
/// 一个点位（一个可读的物理量或状态）。驱动在 <see cref="ExchangeStep.Completed"/> 中按 <see cref="Key"/> 上报数值。
/// </summary>
/// <param name="Key">驱动内唯一的稳定标识，如 "voltage"；一经发布不得改名</param>
/// <param name="Name">界面显示名，如"输出电压"</param>
/// <param name="Unit">物理单位，如 "V"；无单位填空串。上报的数值已换算到该单位（已乘倍率、加偏移）</param>
/// <param name="Access">读写权限</param>
public sealed record PointInfo(string Key, string Name, string Unit, PointAccess Access);

/// <summary>
/// 命令的安全等级：内核据此决定执行前的检查（安全联锁、仿真模式、二次确认），驱动无法绕过
/// </summary>
public enum CommandSafety
{
    /// <summary>
    /// 写参数（不直接改变输出，如设定值）
    /// </summary>
    Parameter = 1,

    /// <summary>
    /// 输出动作（开关输出、启停）
    /// </summary>
    Output = 2,

    /// <summary>
    /// 高危（高压、大电流、不可逆操作）：每次执行都需要操作员二次确认
    /// </summary>
    Hazardous = 3,
}

/// <summary>
/// 一条命令
/// </summary>
/// <param name="Key">驱动内唯一的稳定标识，如 "output.on"；一经发布不得改名</param>
/// <param name="Name">界面显示名，如"打开输出"</param>
/// <param name="Safety">安全等级，决定内核执行前的检查</param>
public sealed record CommandInfo(string Key, string Name, CommandSafety Safety);

/// <summary>
/// 驱动说明：设备库据此在驱动表中列出驱动，并校验模块地址。
/// </summary>
/// <param name="Id">全局唯一的驱动标识，1–64 个字符，只含小写字母、数字、'.'、'-'、'_'，建议"厂家.型号"，如 "acme.psu-3000"。
/// 一经发布不得改名：已挂载的模块只按它找回驱动。</param>
/// <param name="Name">驱动表中显示的名称，如"Acme 程控电源"，最长 64 个字符</param>
/// <param name="Category">设备分类</param>
/// <param name="Description">一句话说明，显示为驱动表的悬停提示</param>
/// <param name="MinAddress">该设备在总线上允许的最小地址（1–255）</param>
/// <param name="MaxAddress">该设备在总线上允许的最大地址（1–255）</param>
public sealed record DriverInfo(
    string Id,
    string Name,
    DeviceCategory Category,
    string Description,
    int MinAddress,
    int MaxAddress)
{
    public IReadOnlyList<PointInfo> Points { get; init; } = [];

    public IReadOnlyList<CommandInfo> Commands { get; init; } = [];
}
