namespace Solve.Device.Sdk;

/// <summary>
/// 通道的功能：决定一路通道能接到设备拓扑的什么用途上（数字孪生的"接线"）。数值写入契约与配置库，
/// 已有成员不得修改，新增只能追加。
/// </summary>
public enum ChannelFunction
{
    /// <summary>
    /// 供电（程控电源的一路输出）
    /// </summary>
    PowerSupply = 1,

    /// <summary>
    /// 负载（电子负载的一路通道）
    /// </summary>
    Load = 2,

    /// <summary>
    /// 采集（电压、电流、温度等测量通道）
    /// </summary>
    Measure = 3,

    /// <summary>
    /// 切换（继电器、数字输出、气缸电磁阀）
    /// </summary>
    Switch = 4,

    /// <summary>
    /// 检测（数字输入：在位、门禁、急停、压合到位）
    /// </summary>
    Detect = 5,

    /// <summary>
    /// 通信（与被测产品通信的端口，如 CAN / UART 通道）
    /// </summary>
    Communication = 6,
}

/// <summary>
/// 一组功能相同、编号连续的通道，如"输出 1–4"、"继电器 1–16"。设备上一个可以单独接线的端子就是一路通道，
/// 用（组 <see cref="Key"/>，通道号 1..<see cref="Count"/>）定位。
/// </summary>
/// <remarks>
/// <para>单路设备（如单输出电源）也声明一组、数量为 1：没有声明通道的驱动不能接线。</para>
/// <para>一经发布，组标识不得改名、通道号不得改变含义：已有接线只按（组标识，通道号）找回通道。
/// 新版本驱动可以追加组或增大数量，不得删除组或减少数量，否则已有接线会失效。</para>
/// </remarks>
/// <param name="Key">驱动内唯一的稳定标识，格式同点位 Key，如 "output"</param>
/// <param name="Name">界面显示名，如"输出"；界面显示为"输出 3"</param>
/// <param name="Function">这组通道的功能</param>
/// <param name="Count">通道数量，1–<see cref="MaxCount"/></param>
public sealed record ChannelGroupInfo(string Key, string Name, ChannelFunction Function, int Count)
{
    /// <summary>
    /// 一组通道的数量上限：覆盖常见的 64 路继电器板、128 路采集卡，拦截误填的超大数量
    /// </summary>
    public const int MaxCount = 256;

    public const int MaxNameLength = 32;
}
