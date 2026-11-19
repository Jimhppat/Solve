using System.Diagnostics.CodeAnalysis;
using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

namespace Solve.Adapters.Hardware.Drivers;

/// <summary>
/// 程序内置的驱动目录。新增硬件驱动时在 <see cref="Definitions"/> 里登记一项；已发布的 <see cref="DriverId"/> 不得改名。
/// </summary>
public sealed class BuiltInDriverCatalog : IDriverCatalog
{
    /// <summary>
    /// 通用模块：不含协议，只记录名称与地址。在具体硬件驱动接入之前用于登记总线上的模块。
    /// </summary>
    public static readonly DriverId GenericModule = DriverId.Of("generic.module");

    private static readonly DriverDescriptor[] Definitions =
    [
        new(GenericModule, "通用模块", HardwareCategory.General,
            "不含通信协议，只登记模块名称与地址",
            DeviceDescriptor.MinAddress, DeviceDescriptor.MaxAddress),
    ];

    private readonly Dictionary<DriverId, DriverDescriptor> _byId;

    public BuiltInDriverCatalog()
        : this(Definitions)
    {
    }

    /// <summary>
    /// 供测试注入自定义目录
    /// </summary>
    public BuiltInDriverCatalog(IEnumerable<DriverDescriptor> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        _byId = [];
        foreach (var driver in drivers)
        {
            driver.EnsureValid();
            if (!_byId.TryAdd(driver.Id, driver))
                throw new InvalidOperationException($"驱动标识重复：{driver.Id}。");
        }

        All = _byId.Values
            .OrderBy(d => HardwareCategoryOrder(d.Category))
            .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    public IReadOnlyList<DriverDescriptor> All { get; }

    public bool TryGet(DriverId id, [NotNullWhen(true)] out DriverDescriptor? driver)
        => _byId.TryGetValue(id, out driver);

    private static int HardwareCategoryOrder(HardwareCategory category)
    {
        for (var i = 0; i < HardwareCategories.All.Count; i++)
        {
            if (HardwareCategories.All[i] == category)
                return i;
        }

        return int.MaxValue;
    }
}
