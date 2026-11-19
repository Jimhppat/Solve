using System.Diagnostics.CodeAnalysis;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Application.Drivers;

/// <summary>
/// 驱动目录出站端口：程序内置的全部驱动。由硬件适配器提供，内容在进程生命周期内不变，可被任意线程无锁读取。
/// </summary>
public interface IDriverCatalog
{
    /// <summary>
    /// 全部驱动，按分类、名称排序
    /// </summary>
    IReadOnlyList<DriverDescriptor> All { get; }

    bool TryGet(DriverId id, [NotNullWhen(true)] out DriverDescriptor? driver);
}
