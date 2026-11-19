using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 总线挂载模块用例：按驱动批量添加、修改、删除一条总线上的模块。只管配置，不访问硬件。
/// </summary>
/// <remarks>
/// <para>不受总线状态限制：总线连接、断开、故障时都可以增删改。目前没有轮询，改配置不触及硬件；
/// 将来接入轮询后，由协议层订阅配置变化并在线生效。</para>
/// <para>各方法会执行同步磁盘 I/O（见 <see cref="Ports.IDeviceRepository"/>），界面调用时放到线程池上执行。
/// 删除总线时其下模块由存储随之删除，不经过本服务。</para>
/// </remarks>
public interface IDeviceService
{
    /// <summary>
    /// 可选的驱动（模块配置窗口左栏的驱动表）
    /// </summary>
    IDriverCatalog Drivers { get; }

    /// <summary>
    /// 一条总线上的全部模块，按地址升序
    /// </summary>
    Task<Result<IReadOnlyList<DeviceDescriptor>>> ListAsync(BusId busId, CancellationToken cancellationToken);

    /// <summary>
    /// 一条总线上的模块数量，供删除总线前的确认提示使用
    /// </summary>
    Task<Result<int>> CountAsync(BusId busId, CancellationToken cancellationToken);

    /// <summary>
    /// 用同一个驱动从起始地址起按连续地址添加若干模块，全部成功或全部不写；成功时返回新增的模块
    /// </summary>
    Task<Result<IReadOnlyList<DeviceDescriptor>>> AddBatchAsync(DeviceBatchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 修改驱动、名称、地址、备注；新驱动须在驱动目录中，地址须在其范围内
    /// </summary>
    Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken);

    /// <summary>
    /// 在同一事务内删除一批模块，返回实际删除的个数
    /// </summary>
    Task<Result<int>> DeleteAsync(IReadOnlyList<DeviceDescriptor> devices, CancellationToken cancellationToken);
}
