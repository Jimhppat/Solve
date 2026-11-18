using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses.TestSupport;

/// <summary>
/// 仿真工厂替身：FailWith 非空时创建失败，用于覆盖“新通道创建失败”路径
/// </summary>
internal sealed class SwitchableTransportFactory : IBusTransportFactory
{
    private readonly SimulatedTransportFactory _inner = new();

    public Error? FailWith { get; set; }

    public Result<IBusTransport> Create(BusDescriptor descriptor)
        => FailWith is { } error ? Result<IBusTransport>.Failure(error) : _inner.Create(descriptor);

    /// <summary>
    /// 取得某条总线最近一次创建的仿真通道
    /// </summary>
    public bool TryGet(BusId busId, out SimulatedTransport transport) => _inner.TryGet(busId, out transport);
}
