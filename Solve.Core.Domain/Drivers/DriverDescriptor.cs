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
        if (!Enum.IsDefined(Category))
            throw new InvalidOperationException($"驱动 {Id} 的分类未定义：{(int)Category}。");
        if (MinAddress < DeviceDescriptor.MinAddress || MaxAddress > DeviceDescriptor.MaxAddress || MinAddress > MaxAddress)
        {
            throw new InvalidOperationException(
                $"驱动 {Id} 的地址范围 [{MinAddress}, {MaxAddress}] 必须在 [{DeviceDescriptor.MinAddress}, {DeviceDescriptor.MaxAddress}] 之内且下限不大于上限。");
        }
    }
}
