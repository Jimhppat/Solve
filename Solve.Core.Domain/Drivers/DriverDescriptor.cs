using Solve.Core.Domain.Devices;

namespace Solve.Core.Domain.Drivers;

/// <summary>
/// 驱动目录中的一项：一种硬件的驱动说明。目前只描述"叫什么、属于哪类、地址能取多少"，协议与点位待协议层确定后扩展。
/// </summary>
/// <param name="MinAddress">该硬件在总线上允许的最小地址，须在模块地址的绝对范围内</param>
/// <param name="MaxAddress">该硬件在总线上允许的最大地址</param>
public sealed record DriverDescriptor(
    DriverId Id,
    string Name,
    HardwareCategory Category,
    string Description,
    int MinAddress,
    int MaxAddress)
{
    public const int MaxNameLength = 64;

    /// <summary>
    /// 驱动声明的通道能力（可以单独接线的端子），按组声明。每台模块再按自己的通道配置选择启用哪几路（见 <see cref="Devices.ModuleChannels"/>）
    /// </summary>
    public IReadOnlyList<ChannelGroup> Channels { get; init; } = [];

    /// <summary>
    /// 通道组名称的最大长度（手工定义的通道组同样适用）
    /// </summary>
    public const int MaxChannelNameLength = 32;

    /// <summary>
    /// 通道由模块实例手工定义（通用模块：没有协议，系统不知道它有哪些端子）。为 true 时 <see cref="Channels"/> 为空
    /// </summary>
    public bool AllowsManualChannels { get; init; }

    /// <summary>
    /// 按组标识查通道组
    /// </summary>
    public ChannelGroup? FindChannelGroup(string? key)
    {
        foreach (var group in Channels)
        {
            if (string.Equals(group.Key, key, StringComparison.Ordinal))
                return group;
        }

        return null;
    }

    /// <summary>
    /// 该驱动允许的地址个数，即一次最多添加的模块数
    /// </summary>
    public int AddressCapacity => MaxAddress - MinAddress + 1;

    public bool AcceptsAddress(int address) => address >= MinAddress && address <= MaxAddress;

    /// <summary>
    /// 驱动目录是代码常量，构造即校验：配置错误在启动时暴露，而不是等到添加模块时
    /// </summary>
    public void EnsureValid()
    {
        if (Id.IsEmpty)
            throw new InvalidOperationException("驱动标识不能为空。");
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException($"驱动 {Id} 的名称不能为空。");
        if (Name.Length > MaxNameLength)
            throw new InvalidOperationException($"驱动 {Id} 的名称不能超过 {MaxNameLength} 个字符。");
        if (!Enum.IsDefined(Category))
            throw new InvalidOperationException($"驱动 {Id} 的分类未定义：{(int)Category}。");
        if (MinAddress < DeviceDescriptor.MinAddress || MaxAddress > DeviceDescriptor.MaxAddress || MinAddress > MaxAddress)
        {
            throw new InvalidOperationException(
                $"驱动 {Id} 的地址范围 [{MinAddress}, {MaxAddress}] 必须在 [{DeviceDescriptor.MinAddress}, {DeviceDescriptor.MaxAddress}] 之内且下限不大于上限。");
        }
    }
}
