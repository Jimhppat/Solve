using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Buses.Ports;

/// <summary>
/// 按总线描述创建物理通道。由组合根选择实现：真实硬件工厂或仿真工厂（默认仿真，安全路径）。
/// </summary>
public interface IBusTransportFactory
{
    /// <summary>
    /// 创建未打开的通道，不做任何 I/O。描述无效返回 Bus.Transport.InvalidDescriptor，
    /// 类型不支持返回 Bus.Transport.UnsupportedDescriptor。返回的通道由调用方负责 Dispose。
    /// </summary>
    Result<IBusTransport> Create(BusDescriptor descriptor);
}
