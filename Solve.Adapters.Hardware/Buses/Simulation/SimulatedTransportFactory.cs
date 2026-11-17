using System.Collections.Concurrent;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Hardware.Buses.Simulation;

/// <summary>
/// 仿真通道工厂：对任何受支持的总线描述都返回 <see cref="SimulatedTransport"/>，不触碰真实硬件。
/// 组合根在未获明确授权连接实机时应使用此工厂（Dry-Run 默认路径）。
/// </summary>
public sealed class SimulatedTransportFactory : IBusTransportFactory
{
    // 按 BusId 保留最近一次创建的实例，供测试/联调界面做故障注入。数量受总线配置数约束，不会无界增长。
    private readonly ConcurrentDictionary<BusId, SimulatedTransport> _created = new();

    public Result<IBusTransport> Create(BusDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 零信任：即使上游已校验，也不基于无效配置创建通道
        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result<IBusTransport>.Failure(BusErrors.InvalidDescriptor(errors));

        // 与真实工厂保持一致的类型支持范围，避免"仿真能跑、实机不支持"
        if (descriptor is not (SerialBusDescriptor or TcpBusDescriptor))
            return Result<IBusTransport>.Failure(BusErrors.UnsupportedDescriptor(descriptor.GetType()));

        var transport = new SimulatedTransport(descriptor.Id);
        _created[descriptor.Id] = transport;
        return Result<IBusTransport>.Success(transport);
    }

    /// <summary>
    /// 取得某条总线最近一次创建的仿真通道
    /// </summary>
    public bool TryGet(BusId busId, out SimulatedTransport transport)
        => _created.TryGetValue(busId, out transport!);
}
