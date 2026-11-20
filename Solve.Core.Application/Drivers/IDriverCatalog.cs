using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Drivers;

/// <summary>
/// 驱动目录出站端口：程序内置驱动 + 设备库中已信任、已载入且未停用的驱动。
/// </summary>
/// <remarks>
/// 内容可能在运行中变化（载入新驱动包、撤销信任、停用驱动），实现以不可变快照整体替换：
/// 每次调用都读到某一份完整的快照，可被任意线程无锁读取。需要前后一致时先取 <see cref="All"/> 再据此处理。
/// </remarks>
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

    /// <summary>
    /// 目录内容替换之后触发，在执行替换的线程上同步调用。处理程序只能做轻量工作（如投递信号），不得阻塞或抛出异常。
    /// </summary>
    event EventHandler? Changed;
}
