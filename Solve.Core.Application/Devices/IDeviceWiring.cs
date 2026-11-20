using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 模块配置对接线的依赖：删除模块前要知道会拆掉多少接线，换驱动前要确认已有接线的通道在新驱动里仍然存在。
/// 由接线服务实现；模块服务只依赖这个窄接口，不依赖接线的其余部分。
/// </summary>
public interface IDeviceWiring
{
    /// <summary>
    /// 这些模块上已接线的通道数（删除模块会一并拆除）
    /// </summary>
    int CountWires(IReadOnlyCollection<DeviceId> devices);

    /// <summary>
    /// 模块改用 <paramref name="driver"/> 后已有接线是否仍然有效：通道组存在、通道号在范围内、功能相同。
    /// 不满足时返回失败，列出受影响的通道，要求先拆除这些接线。
    /// </summary>
    Result CheckDriverChange(DeviceId device, DriverDescriptor driver);
}
