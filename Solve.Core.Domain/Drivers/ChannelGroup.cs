namespace Solve.Core.Domain.Drivers;

/// <summary>
/// 通道的功能（内核侧），决定一路通道能接到拓扑的什么用途上。与 SDK 的 ChannelFunction 一一对应，映射只在设备库适配器中做一次。
/// </summary>
/// <remarks>
/// 数值写入配置库（接线表的 kind 列），已有成员的数值不得修改，新增只能追加。从 1 开始编号，default 为非法值。
/// </remarks>
public enum ChannelKind
{
    /// <summary>
    /// 供电
    /// </summary>
    PowerSupply = 1,

    /// <summary>
    /// 负载
    /// </summary>
    Load = 2,

    /// <summary>
    /// 采集
    /// </summary>
    Measure = 3,

    /// <summary>
    /// 切换
    /// </summary>
    Switch = 4,

    /// <summary>
    /// 检测
    /// </summary>
    Detect = 5,

    /// <summary>
    /// 通信
    /// </summary>
    Communication = 6,
}

public static class ChannelKinds
{
    public static bool IsDefined(this ChannelKind kind) => kind is >= ChannelKind.PowerSupply and <= ChannelKind.Communication;

    public static string DisplayName(this ChannelKind kind) => kind switch
    {
        ChannelKind.PowerSupply => "供电",
        ChannelKind.Load => "负载",
        ChannelKind.Measure => "采集",
        ChannelKind.Switch => "切换",
        ChannelKind.Detect => "检测",
        ChannelKind.Communication => "通信",
        _ => $"未定义功能({(int)kind})",
    };
}

/// <summary>
/// 驱动声明的一组通道：功能相同、编号 1..<see cref="Count"/> 连续。一路通道用（<see cref="Key"/>，通道号）定位。
/// </summary>
/// <param name="Key">驱动内唯一的稳定标识，接线按它找回通道</param>
/// <param name="Name">界面显示名，如"输出"</param>
public sealed record ChannelGroup(string Key, string Name, ChannelKind Kind, int Count)
{
    public bool Contains(int index) => index >= 1 && index <= Count;

    /// <summary>
    /// 一路通道的显示名：单路组只显示组名，多路组加通道号，如"继电器 3"
    /// </summary>
    public string ChannelName(int index) => Count == 1 ? Name : $"{Name} {index}";
}
