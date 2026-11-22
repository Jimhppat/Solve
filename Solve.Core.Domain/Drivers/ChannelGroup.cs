using System.Globalization;

namespace Solve.Core.Domain.Drivers;

/// <summary>
/// 通道的功能（内核侧），决定一路通道能接到拓扑的什么用途上。与 SDK 的 ChannelFunction 一一对应，映射只在设备库适配器中做一次。
/// </summary>
/// <remarks>
/// 数值写入配置库（接线表的 kind 列、模块通道配置），已有成员的数值不得修改，新增只能追加。从 1 开始编号，default 为非法值。
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
    /// <summary>
    /// 界面下拉的顺序
    /// </summary>
    public static IReadOnlyList<ChannelKind> All { get; } =
    [
        ChannelKind.PowerSupply,
        ChannelKind.Load,
        ChannelKind.Measure,
        ChannelKind.Switch,
        ChannelKind.Detect,
        ChannelKind.Communication,
    ];

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
/// 一组通道：功能相同、编号 1..<see cref="Count"/> 连续。一路通道用（<see cref="Key"/>，通道号）定位。
/// </summary>
/// <remarks>
/// <para>两种来源：驱动声明的是通道<b>能力</b>（<see cref="EnabledIndices"/> 为 null、没有别名）；
/// 按模块实例的通道配置解析后（见 <see cref="Devices.ModuleChannels"/>）带上启用的通道与别名，接线只能用启用的通道。</para>
/// </remarks>
/// <param name="Key">组标识，接线按它找回通道</param>
/// <param name="Name">界面显示名，如"输出"</param>
public sealed record ChannelGroup(string Key, string Name, ChannelKind Kind, int Count)
{
    /// <summary>
    /// 一组通道的数量上限，与 SDK 的 ChannelGroupInfo.MaxCount 一致
    /// </summary>
    public const int MaxCount = 256;

    private static readonly IReadOnlyDictionary<int, string> NoAliases = new Dictionary<int, string>();

    /// <summary>
    /// 启用的通道号（升序）；null 表示全部启用
    /// </summary>
    public IReadOnlySet<int>? EnabledIndices { get; init; }

    /// <summary>
    /// 通道别名（通道号 → 名称），如 3 → "3# 压合气缸"
    /// </summary>
    public IReadOnlyDictionary<int, string> Aliases { get; init; } = NoAliases;

    /// <summary>
    /// 通道号在 1..<see cref="Count"/> 之内（不论是否启用）
    /// </summary>
    public bool Contains(int index) => index >= 1 && index <= Count;

    /// <summary>
    /// 这路通道存在且已启用，可以接线
    /// </summary>
    public bool IsEnabled(int index) => Contains(index) && (EnabledIndices is null || EnabledIndices.Contains(index));

    /// <summary>
    /// 启用的通道号，升序
    /// </summary>
    public IEnumerable<int> Enabled()
    {
        for (var index = 1; index <= Count; index++)
        {
            if (IsEnabled(index))
                yield return index;
        }
    }

    public int EnabledCount => EnabledIndices?.Count(Contains) ?? Count;

    /// <summary>
    /// 一路通道的显示名：有别名用别名，否则为组名 + 通道编号，如"负载 CH3"（单路组同样带编号，如"输出 CH1"）
    /// </summary>
    public string ChannelName(int index)
        => Aliases.TryGetValue(index, out var alias) ? alias : DefaultName(Name, index);

    /// <summary>
    /// 通道编号，如"CH3"。编号即通道号（驱动的 DeviceCommand.Channel、点位上报的通道号），从 1 开始，
    /// 与设备面板 / 端子排上的丝印一致，接线时按它核对，所以任何地方都不另起编号。
    /// </summary>
    public static string Code(int index) => string.Create(CultureInfo.InvariantCulture, $"CH{index}");

    /// <summary>
    /// 不带别名的通道名，如"负载 CH3"：组名区分同一模块上的多组通道（如"输出 CH1"与"继电器 CH1"）
    /// </summary>
    public static string DefaultName(string groupName, int index) => $"{groupName} {Code(index)}";
}
