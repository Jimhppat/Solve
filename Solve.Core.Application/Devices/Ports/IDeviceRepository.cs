using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Devices.Ports;

/// <summary>
/// 总线挂载设备的存储出站端口。
/// </summary>
/// <remarks>
/// <para>实现可能执行同步磁盘 I/O（Microsoft.Data.Sqlite 的 Async 方法实际为同步实现），调用方不要在 UI 线程上直接 await。</para>
/// <para>存储层面的失败返回失败的 Result，不抛异常。删除总线时其下设备随之删除，由存储在同一事务内完成。</para>
/// </remarks>
public interface IDeviceRepository
{
    /// <summary>
    /// 读取一条总线上的全部设备，按地址升序。无法解析的记录不返回，由实现记录错误日志。
    /// </summary>
    Task<Result<IReadOnlyList<DeviceDescriptor>>> ListByBusAsync(BusId busId, CancellationToken cancellationToken);

    /// <summary>
    /// 一条总线上的设备数量（含无法解析的记录），供删除总线前的确认提示使用。
    /// </summary>
    Task<Result<int>> CountByBusAsync(BusId busId, CancellationToken cancellationToken);

    /// <summary>
    /// 新增。地址与同总线其他设备冲突返回 Device.AddressConflict；总线不存在返回 Device.BusNotFound。
    /// </summary>
    Task<Result> AddAsync(DeviceDescriptor device, CancellationToken cancellationToken);

    /// <summary>
    /// 修改名称、地址、备注。所属总线不可变：Id 不存在或不在该总线上返回 Device.Storage.NotFound。
    /// </summary>
    Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken);

    /// <summary>
    /// 删除。记录不存在返回 Device.Storage.NotFound。
    /// </summary>
    Task<Result> DeleteAsync(DeviceId id, CancellationToken cancellationToken);
}
