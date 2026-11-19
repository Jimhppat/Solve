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
    /// 驱动未加载：驱动包不在设备库、未经确认或加载失败
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

    public DeviceContext? Context { get; init; }
}

/// <summary>
/// 把模块配置绑定到驱动：找驱动实现 → 校验地址 → 补全参数 → 创建会话。纯内存操作，不访问存储与硬件。
/// </summary>
/// <remarks>
/// 调用驱动的 <see cref="IDeviceDriver.CreateSession"/> 是执行第三方代码的边界：任何异常都只让这一个模块绑定失败。
/// </remarks>
public sealed class DriverBinder
{
    private readonly IDriverCatalog _catalog;
    private readonly ILogger _logger;

    public DriverBinder(IDriverCatalog catalog, ILogger<DriverBinder> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);
        _catalog = catalog;
        _logger = logger;
    }

    public DriverBinding Bind(DeviceDescriptor device, LinkKind link)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!_catalog.TryGetImplementation(device.Driver, out var driver))
        {
            return _catalog.TryGet(device.Driver, out var known)
                ? new DriverBinding(device, DriverBindingStatus.NoProtocol, $"{known.Name}不含通信协议。")
                : new DriverBinding(device, DriverBindingStatus.DriverNotLoaded,
                    $"驱动「{device.Driver}」未加载：驱动包不在设备库、未经确认或加载失败。");
        }

        var info = driver.Info;
        if (device.Address < info.MinAddress || device.Address > info.MaxAddress)
        {
            return new DriverBinding(device, DriverBindingStatus.AddressOutOfRange,
                $"{info.Name}的地址必须在 [{info.MinAddress}, {info.MaxAddress}] 之间，当前为 {device.Address}。");
        }

        var parameters = DriverParameters.Resolve(info, device.Parameters);
        if (parameters.IsFailure)
            return new DriverBinding(device, DriverBindingStatus.InvalidParameters, parameters.Error.Message);

        var context = new DeviceContext(device.Address, link, parameters.Value);
        try
        {
            var session = driver.CreateSession(context);
            if (session is null)
                return new DriverBinding(device, DriverBindingStatus.SessionFailed, $"{info.Name}没有返回会话。");

            return new DriverBinding(device, DriverBindingStatus.Ready, null) { Session = session, Context = context };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "驱动 {Driver} 为{Device}创建会话失败", device.Driver, device);
            return new DriverBinding(device, DriverBindingStatus.SessionFailed, $"{info.Name}创建会话失败：{ex.Message}");
        }
    }
}
