using Solve.Core.Domain.Common;

namespace Solve.Core.Application.DeviceLibrary;

/// <summary>
/// 驱动包在设备库中的状态
/// </summary>
public enum DriverPackageStatus
{
    /// <summary>
    /// 已加载，其驱动出现在驱动表中
    /// </summary>
    Loaded = 1,

    /// <summary>
    /// 新出现或内容有变（文件指纹与上次确认的不同），等待操作员确认，未加载
    /// </summary>
    PendingApproval = 2,

    /// <summary>
    /// 已确认，重启程序后加载
    /// </summary>
    ApprovedRestartRequired = 3,

    /// <summary>
    /// 已撤销确认，重启程序后不再加载（本次运行仍在使用）
    /// </summary>
    RevokedRestartRequired = 4,

    /// <summary>
    /// 需要的 SDK 版本与本程序不一致
    /// </summary>
    Incompatible = 5,

    /// <summary>
    /// 清单缺失或内容无效
    /// </summary>
    Invalid = 6,

    /// <summary>
    /// 包标识或驱动标识与其他包（或内置驱动）重复，涉及的包都不加载
    /// </summary>
    Conflict = 7,

    /// <summary>
    /// 已确认但加载失败（程序集无法加载、入口类型无效、驱动说明无效）
    /// </summary>
    LoadFailed = 8,
}

/// <summary>
/// 设备库中一个驱动包的快照
/// </summary>
/// <param name="Id">包标识（清单中的 id）；清单无法读取时为目录名</param>
/// <param name="Directory">包所在目录名（相对设备库根目录）</param>
/// <param name="Fingerprint">包内全部文件的 SHA-256 指纹（十六进制），确认时按它记忆；清单无法读取时为空串</param>
/// <param name="Problem">非正常状态的原因，正常时为 null</param>
/// <param name="DriverNames">包内驱动的名称；未加载时为空</param>
public sealed record DriverPackageInfo(
    string Id,
    string Directory,
    string Name,
    string Version,
    string Author,
    string Fingerprint,
    DriverPackageStatus Status,
    string? Problem,
    IReadOnlyList<string> DriverNames)
{
    /// <summary>
    /// 可以被操作员确认（启用）
    /// </summary>
    public bool CanApprove => Status is DriverPackageStatus.PendingApproval or DriverPackageStatus.RevokedRestartRequired;

    /// <summary>
    /// 可以撤销确认
    /// </summary>
    public bool CanRevoke => Status is DriverPackageStatus.Loaded or DriverPackageStatus.ApprovedRestartRequired;
}

/// <summary>
/// 设备库：第三方驱动包的发现、确认与加载状态。驱动包只在程序启动时加载；确认或撤销在下次启动时生效。
/// </summary>
/// <remarks>
/// 信任策略：包第一次出现、或任一文件内容改变（指纹变化）时都处于待确认状态，经操作员确认后才会加载，
/// 确认记录按指纹保存，哪怕只改一个字节也需要重新确认。
/// 方法可能执行磁盘 I/O（读取包文件、写设置库），界面调用时放到线程池上执行。
/// </remarks>
public interface IDeviceLibrary
{
    /// <summary>
    /// 设备库根目录（完整路径）
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// 最近一次扫描得到的全部驱动包，按包标识排序
    /// </summary>
    IReadOnlyList<DriverPackageInfo> Packages { get; }

    /// <summary>
    /// 重新扫描设备库目录，发现新放入或已改动的包。不加载新包：已加载的包保持不变，新包需确认并重启后加载。
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> RescanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 确认（启用）一个包。<paramref name="fingerprint"/> 必须与当前扫描到的一致，防止确认之后、重启之前包被替换。
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> ApproveAsync(string packageId, string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// 撤销确认：下次启动起不再加载该包
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> RevokeAsync(string packageId, CancellationToken cancellationToken);
}

/// <summary>
/// 设备库错误码
/// </summary>
public static class DeviceLibraryErrors
{
    public const string PackageNotFoundCode = "DeviceLibrary.PackageNotFound";
    public const string FingerprintChangedCode = "DeviceLibrary.FingerprintChanged";
    public const string NotApprovableCode = "DeviceLibrary.NotApprovable";
    public const string ScanFailedCode = "DeviceLibrary.ScanFailed";

    public static Error PackageNotFound(string packageId)
        => new(PackageNotFoundCode, $"设备库中找不到驱动包「{packageId}」，可能已被移走，请重新扫描。");

    public static Error FingerprintChanged(string packageId)
        => new(FingerprintChangedCode, $"驱动包「{packageId}」的文件在确认前发生了变化，请重新扫描后再确认。");

    public static Error NotApprovable(string packageId, DriverPackageStatus status)
        => new(NotApprovableCode, $"驱动包「{packageId}」当前状态为 {status}，不能执行此操作。");

    public static Error ScanFailed(string detail)
        => new(ScanFailedCode, $"扫描设备库失败：{detail}");
}
