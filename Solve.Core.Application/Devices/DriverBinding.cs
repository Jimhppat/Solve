using Microsoft.Extensions.Logging;
using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Devices;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 模块与驱动的绑定状态
/// </summary>
public enum DriverBindingStatus
{
    /// <summary>
    /// 已就绪：拿到了驱动会话，可以通信
    /// </summary>
    Ready = 1,

    /// <summary>
    /// 驱动不含协议（如通用模块），不通信
    /// </summary>
    NoProtocol = 2,

    /// <summary>
    /// 驱动未加载：驱动包不在设备库、未经信任、已停用或载入失败
    /// </summary>
    DriverNotLoaded = 3,

    /// <summary>
    /// 地址不在驱动允许的范围内
    /// </summary>
    AddressOutOfRange = 4,

    /// <summary>
    /// 模块参数不合法（如驱动升级后收窄了范围）
    /// </summary>
    InvalidParameters = 5,

    /// <summary>
    /// 驱动创建会话时出错，只影响这一个模块
    /// </summary>
    SessionFailed = 6,
}

/// <summary>
/// 一个模块的绑定结果。<see cref="Status"/> 为 <see cref="DriverBindingStatus.Ready"/> 时 <see cref="Session"/> 与 <see cref="Context"/> 非空。
/// </summary>
/// <remarks>
/// 模块或所在总线的配置变化后会重新绑定，会话随之重建（会话内的协议状态从头开始）。
/// </remarks>
/// <param name="Problem">非就绪时的原因（面向操作员）</param>
public sealed record DriverBinding(DeviceDescriptor Device, DriverBindingStatus Status, string? Problem)
{
    public IDeviceSession? Session { get; init; }

    /// <summary>
    /// 驱动说明（点位、命令、参数的声明）；有驱动实现时非空
    /// </summary>
    public DriverInfo? Driver { get; init; }

    public DeviceContext? Context { get; init; }

    /// <summary>
    /// 绑定时使用的驱动实现；没有实现时为 null。驱动目录变化后据此判断绑定是否过期（按引用比较）
    /// </summary>
    public IDeviceDriver? Implementation { get; init; }
}

/// <summary>
/// 把模块配置绑定到驱动：找驱动实现 → 校验地址 → 补全参数 → 创建会话。纯内存操作，不访问存储与硬件。
/// </summary>
/// <remarks>
/// 调用驱动的 <see cref="IDeviceDriver.CreateSession"/> 是执行第三方代码的边界：任何异常都只让这一个模块绑定失败。
/// </remarks>
public sealed class DriverBinder
{
    private readonly ILogger _logger;

    public DriverBinder(IDriverCatalog catalog, ILogger<DriverBinder> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);
        Catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// 绑定所依据的驱动目录；目录变化（<see cref="IDriverCatalog.Changed"/>）后用 <see cref="IsCurrent"/> 找出过期的绑定
    /// </summary>
    public IDriverCatalog Catalog { get; }

    public DriverBinding Bind(DeviceDescriptor device, LinkKind link)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!Catalog.TryGetImplementation(device.Driver, out var driver))
        {
            return Catalog.TryGet(device.Driver, out var known)
                ? new DriverBinding(device, DriverBindingStatus.NoProtocol, $"{known.Name}不含通信协议。")
                : new DriverBinding(device, DriverBindingStatus.DriverNotLoaded,
                    $"驱动「{device.Driver}」未加载：驱动包不在设备库、未经信任、已停用或载入失败。");
        }

        var info = driver.Info;
        if (device.Address < info.MinAddress || device.Address > info.MaxAddress)
        {
            return new DriverBinding(device, DriverBindingStatus.AddressOutOfRange,
                $"{info.Name}的地址必须在 [{info.MinAddress}, {info.MaxAddress}] 之间，当前为 {device.Address}。") { Implementation = driver };
        }

        var parameters = DriverParameters.Resolve(info, device.Parameters);
        if (parameters.IsFailure)
            return new DriverBinding(device, DriverBindingStatus.InvalidParameters, parameters.Error.Message) { Implementation = driver };

        var context = new DeviceContext(device.Address, link, parameters.Value);
        try
        {
            var session = driver.CreateSession(context);
            if (session is null)
                return new DriverBinding(device, DriverBindingStatus.SessionFailed, $"{info.Name}没有返回会话。") { Implementation = driver };

            return new DriverBinding(device, DriverBindingStatus.Ready, null)
            {
                Session = session,
                Context = context,
                Driver = info,
                Implementation = driver,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "驱动 {Driver} 为{Device}创建会话失败", device.Driver, device);
            return new DriverBinding(device, DriverBindingStatus.SessionFailed, $"{info.Name}创建会话失败：{ex.Message}") { Implementation = driver };
        }
    }

    /// <summary>
    /// 按当前驱动目录，这个绑定是否仍然有效：所用驱动实现没变（按引用），或仍然没有实现且"内置 / 未载入"的判断没变。
    /// 有效的绑定可以原样保留，会话与轮询计数不必重建。
    /// </summary>
    public bool IsCurrent(DriverBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (Catalog.TryGetImplementation(binding.Device.Driver, out var implementation))
            return ReferenceEquals(implementation, binding.Implementation);

        if (binding.Implementation is not null)
            return false;

        return Catalog.TryGet(binding.Device.Driver, out _)
            ? binding.Status == DriverBindingStatus.NoProtocol
            : binding.Status == DriverBindingStatus.DriverNotLoaded;
    }
}
