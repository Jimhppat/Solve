using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 总线挂载设备用例：查看、新增、修改、删除一条总线上的设备。只管配置，不访问硬件。
/// </summary>
/// <remarks>
/// 各方法会执行同步磁盘 I/O（见 <see cref="Ports.IDeviceRepository"/>），界面调用时放到线程池上执行。
/// 删除总线时其下设备由存储随之删除，不经过本服务。
/// </remarks>
public interface IDeviceService
{
    /// <summary>
    /// 一条总线上的全部设备，按地址升序
    /// </summary>
    Task<Result<IReadOnlyList<DeviceDescriptor>>> ListAsync(BusId busId, CancellationToken cancellationToken);

    /// <summary>
    /// 一条总线上的设备数量，供删除总线前的确认提示使用
    /// </summary>
    Task<Result<int>> CountAsync(BusId busId, CancellationToken cancellationToken);

    Task<Result> AddAsync(DeviceDescriptor device, CancellationToken cancellationToken);

    Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(DeviceDescriptor device, CancellationToken cancellationToken);
}
