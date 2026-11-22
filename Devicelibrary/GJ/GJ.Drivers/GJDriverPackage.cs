using GJ.Drivers.Da750;
using Solve.Device.Sdk;

namespace GJ.Drivers;

/// <summary>
/// 驱动包入口：package.json 的 entryType 指向这里。构造与 Drivers 只做声明，不访问任何外部资源。
/// </summary>
public sealed class GJDriverPackage : IDriverPackage
{
    public IReadOnlyList<IDeviceDriver> Drivers { get; } = [new Da750Driver(Da750Model.Da750_4_60A)];
}
