using Microsoft.Extensions.Logging;
using Solve.Adapters.Hardware.Buses.Serial;
using Solve.Adapters.Hardware.Buses.Tcp;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Hardware.Buses;

/// <summary>
/// 真实硬件通道工厂：串口 → <see cref="SerialPortTransport"/>，TCP → <see cref="TcpTransport"/>。
/// </summary>
/// <remarks>
/// 会触碰实机链路。组合根只有在获得明确授权后才应选用此工厂，默认应使用仿真工厂（Dry-Run）。
/// 创建本身不做任何 I/O，只有 BusRuntime 执行 Connect 时才会打开端口。
/// </remarks>
public sealed class HardwareTransportFactory : IBusTransportFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public HardwareTransportFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
    }

    public Result<IBusTransport> Create(BusDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var errors = descriptor.Validate();
        if (errors.Count > 0)
            return Result<IBusTransport>.Failure(BusErrors.InvalidDescriptor(errors));

        IBusTransport? transport = descriptor switch
        {
            SerialBusDescriptor serial => new SerialPortTransport(serial, _loggerFactory.CreateLogger<SerialPortTransport>()),
            TcpBusDescriptor tcp => new TcpTransport(tcp, _loggerFactory.CreateLogger<TcpTransport>()),
            _ => null,
        };

        return transport is null
            ? Result<IBusTransport>.Failure(BusErrors.UnsupportedDescriptor(descriptor.GetType()))
            : Result<IBusTransport>.Success(transport);
    }
}
