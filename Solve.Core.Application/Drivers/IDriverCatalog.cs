using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Drivers;

/// <summary>
/// 驱动目录出站端口：程序内置驱动 + 设备库本次启动加载的驱动。内容在进程生命周期内不变，可被任意线程无锁读取。
/// </summary>
public interface IDriverCatalog
{
    /// <summary>
    /// 全部驱动，按分类、名称排序
    /// </summary>
    IReadOnlyList<DriverDescriptor> All { get; }

    bool TryGet(DriverId id, [NotNullWhen(true)] out DriverDescriptor? driver);

    /// <summary>
    /// 驱动实现（来自设备库的驱动包）。程序内置的"通用模块"不含协议，没有实现。
    /// </summary>
    bool TryGetImplementation(DriverId id, [NotNullWhen(true)] out IDeviceDriver? implementation);
}
