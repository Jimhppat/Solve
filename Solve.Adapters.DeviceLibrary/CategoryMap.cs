using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// SDK 设备分类 → 内核硬件分类的唯一映射处。SDK 追加分类时必须同时在这里与 <see cref="HardwareCategory"/> 中追加，
/// 架构测试会检查 SDK 的每个分类都有映射，防止驱动包因"分类未定义"整体载入失败。
/// </summary>
public static class CategoryMap
{
    public static bool TryMap(DeviceCategory category, out HardwareCategory mapped)
    {
        HardwareCategory? result = category switch
        {
            DeviceCategory.General => HardwareCategory.General,
            DeviceCategory.PowerAndLoad => HardwareCategory.PowerAndLoad,
            _ => null,
        };

        mapped = result ?? default;
        return result is not null;
    }
}
