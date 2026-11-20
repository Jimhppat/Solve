using Chroma.Drivers.Psu62000B;
using Solve.Device.Sdk;

namespace Chroma.Drivers;

/// <summary>
/// 驱动包入口：package.json 的 entryType 指向这里。构造与 Drivers 只做声明，不访问任何外部资源。
/// </summary>
public sealed class ChromaDriverPackage : IDriverPackage
{
    public IReadOnlyList<IDeviceDriver> Drivers { get; } = [new Chroma62000BDriver(Chroma62000BModel.M62015B_15_90)];
}
