using System.Diagnostics.CodeAnalysis;
using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 驱动目录：程序自带的驱动 + 设备库中已加载的驱动包。构造后不可变，可被任意线程无锁读取。
/// </summary>
public sealed class DriverCatalog : IDriverCatalog
{
    /// <summary>
    /// 通用模块：不含协议，只记录名称与地址，程序自带。已发布的标识不得改名。
    /// </summary>
    public static readonly DriverId GenericModule = DriverId.Of("generic.module");

    /// <summary>
    /// 程序自带的驱动
    /// </summary>
    public static IReadOnlyList<DriverDescriptor> BuiltIns { get; } =
    [
        new(GenericModule, "通用模块", HardwareCategory.General,
            "不含通信协议，只登记模块名称与地址",
            DeviceDescriptor.MinAddress, DeviceDescriptor.MaxAddress),
    ];

    private readonly Dictionary<DriverId, DriverDescriptor> _byId;
    private readonly IReadOnlyDictionary<DriverId, IDeviceDriver> _implementations;

    /// <summary>
    /// 只含程序自带的驱动（设备库不可用时使用）
    /// </summary>
    public DriverCatalog()
        : this(BuiltIns)
    {
    }

    /// <param name="implementations">驱动实现（来自驱动包），键必须都在 <paramref name="drivers"/> 中</param>
    public DriverCatalog(IEnumerable<DriverDescriptor> drivers, IReadOnlyDictionary<DriverId, IDeviceDriver>? implementations = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        _implementations = implementations ?? new Dictionary<DriverId, IDeviceDriver>();
        _byId = [];
        foreach (var driver in drivers)
        {
            driver.EnsureValid();
            if (!_byId.TryAdd(driver.Id, driver))
                throw new InvalidOperationException($"驱动标识重复：{driver.Id}。");
        }

        All = _byId.Values
            .OrderBy(d => CategoryOrder(d.Category))
            .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            .ToArray();

        foreach (var id in _implementations.Keys)
        {
            if (!_byId.ContainsKey(id))
                throw new InvalidOperationException($"驱动实现 {id} 没有对应的驱动说明。");
        }
    }

    public IReadOnlyList<DriverDescriptor> All { get; }

    public bool TryGet(DriverId id, [NotNullWhen(true)] out DriverDescriptor? driver)
        => _byId.TryGetValue(id, out driver);

    public bool TryGetImplementation(DriverId id, [NotNullWhen(true)] out IDeviceDriver? implementation)
        => _implementations.TryGetValue(id, out implementation);

    private static int CategoryOrder(HardwareCategory category)
    {
        for (var i = 0; i < HardwareCategories.All.Count; i++)
        {
            if (HardwareCategories.All[i] == category)
                return i;
        }

        return int.MaxValue;
    }
}
