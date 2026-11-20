using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Application.DeviceLibrary;

/// <summary>
/// 驱动包在设备库中的状态。数值写入日志与界面，已有成员不得改值，新增只能追加。
/// </summary>
public enum DriverPackageStatus
{
    /// <summary>
    /// 已信任并载入，其驱动（未停用的）出现在驱动表中
    /// </summary>
    Loaded = 1,

    /// <summary>
    /// 新发现，或内容有变（文件指纹与信任记录不同），等待操作员决定；不执行包内任何代码
    /// </summary>
    PendingApproval = 2,

    /// <summary>
    /// 已信任新版本，但本次运行已载入旧版本：已载入的代码无法安全替换，重启后换成新版本
    /// </summary>
    ApprovedRestartRequired = 3,

    /// <summary>
    /// 已撤销信任：驱动立即从驱动表移除、不再被调用；代码在重启前仍留在内存中（.NET 无法安全卸载）
    /// </summary>
    Revoked = 4,

    /// <summary>
    /// 需要的 SDK 版本与本程序不一致
    /// </summary>
    Incompatible = 5,

    /// <summary>
    /// 清单缺失或内容无效
    /// </summary>
    Invalid = 6,

    /// <summary>
    /// 包标识或驱动标识与其他包（或内置驱动）重复
    /// </summary>
    Conflict = 7,

    /// <summary>
    /// 已信任但载入失败（程序集无法加载、入口类型无效、驱动说明无效或与清单声明不一致）
    /// </summary>
    LoadFailed = 8,

    /// <summary>
    /// 操作员选择忽略这个版本：不再作为"新发现"提示，内容一变又会回到待确认；仍可随时信任
    /// </summary>
    Ignored = 9,
}

/// <summary>
/// 驱动包中的一个驱动。名称、分类、厂家、型号来自清单声明（扫描时不执行代码就能看到），
/// 载入后内核会核对代码中的驱动说明与声明一致。
/// </summary>
/// <param name="Manufacturer">厂家，清单未写时为空串</param>
/// <param name="Model">型号，清单未写时为空串</param>
public sealed record DriverEntryInfo(
    DriverId Id,
    string Name,
    HardwareCategory Category,
    string Manufacturer,
    string Model)
{
    /// <summary>
    /// 代码已载入且与声明一致
    /// </summary>
    public bool IsLoaded { get; init; }

    /// <summary>
    /// 操作员没有停用这个驱动
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// 载入后读到的地址范围；未载入时为 null
    /// </summary>
    public int? MinAddress { get; init; }

    public int? MaxAddress { get; init; }
}

/// <summary>
/// 设备库中一个驱动包的快照
/// </summary>
/// <param name="Id">包标识（清单中的 id）；清单无法读取时为目录名</param>
/// <param name="Directory">包所在目录名（相对设备库根目录）</param>
/// <param name="Fingerprint">包内全部文件的 SHA-256 指纹（十六进制），信任时按它记忆；清单无法读取时可能为空串</param>
/// <param name="Problem">需要操作员注意的说明（非正常状态的原因、本次运行仍在使用旧版本等），没有时为 null</param>
/// <param name="Drivers">清单声明的驱动；清单无效时为空</param>
public sealed record DriverPackageInfo(
    string Id,
    string Directory,
    string Name,
    string Version,
    string Author,
    string Fingerprint,
    DriverPackageStatus Status,
    string? Problem,
    IReadOnlyList<DriverEntryInfo> Drivers)
{
    /// <summary>
    /// 曾经信任过这个包的另一个版本（内容已变化）
    /// </summary>
    public bool WasApproved { get; init; }

    /// <summary>
    /// 本次运行正在使用这个包的代码（已载入且仍受信任），即使磁盘上的文件已被替换或移走
    /// </summary>
    public bool IsInUse { get; init; }

    /// <summary>
    /// 可以信任（并立即载入）
    /// </summary>
    public bool CanApprove => Status is DriverPackageStatus.PendingApproval or DriverPackageStatus.Revoked or DriverPackageStatus.Ignored;

    /// <summary>
    /// 可以撤销信任
    /// </summary>
    public bool CanRevoke => Status is DriverPackageStatus.Loaded or DriverPackageStatus.ApprovedRestartRequired || IsInUse;

    /// <summary>
    /// 可以忽略这个版本
    /// </summary>
    public bool CanIgnore => Status == DriverPackageStatus.PendingApproval;

    /// <summary>
    /// 可以逐个启用 / 停用其中的驱动
    /// </summary>
    public bool CanToggleDrivers => IsInUse;
}

/// <summary>
/// 设备库：第三方驱动包的扫描、导入、信任、载入与驱动启停。
/// </summary>
/// <remarks>
/// <para>信任策略：扫描只读清单与文件，不执行代码；包第一次出现或任一文件改变（指纹变化）都需要操作员信任，
/// 信任记录按指纹保存。信任后立即载入并出现在驱动表中，不需要重启；
/// 已载入的代码无法安全卸载，所以替换已载入的版本需要重启，撤销信任则立即把驱动从驱动表移除。</para>
/// <para>线程：方法互斥执行，可能做磁盘 I/O 与载入程序集，界面调用时放到线程池上执行。
/// <see cref="Packages"/> 可被任意线程读取。</para>
/// </remarks>
public interface IDeviceLibrary
{
    /// <summary>
    /// 设备库根目录（完整路径）
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// 最近一次扫描得到的全部驱动包：新发现与有变化的排在前面，其余按包标识排序
    /// </summary>
    IReadOnlyList<DriverPackageInfo> Packages { get; }

    /// <summary>
    /// 重新扫描设备库目录，发现新放入或已改动的包。只读文件，不执行任何代码。
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> RescanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 信任一个包并立即载入。<paramref name="fingerprint"/> 必须与磁盘上当前的内容一致，防止确认之后包被替换。
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> ApproveAsync(string packageId, string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// 撤销信任：驱动立即从驱动表移除，下次启动起不再载入
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> RevokeAsync(string packageId, CancellationToken cancellationToken);

    /// <summary>
    /// 忽略这个版本：不再作为新发现提示，直到内容改变
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> IgnoreAsync(string packageId, string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// 启用或停用一个已载入的驱动。停用后驱动从驱动表移除，使用它的模块不再轮询；设置持久保存。
    /// </summary>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> SetDriverEnabledAsync(DriverId driverId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// 把一个目录（如驱动包的编译输出）作为驱动包复制进设备库，目标目录名为包标识。导入后处于待确认状态，不执行代码。
    /// </summary>
    /// <param name="overwrite">设备库中已有同一包的不同版本时是否替换；为 false 时返回 <see cref="DeviceLibraryErrors.AlreadyExistsCode"/></param>
    Task<Result<IReadOnlyList<DriverPackageInfo>>> ImportAsync(string sourceDirectory, bool overwrite, CancellationToken cancellationToken);
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
    public const string DriverNotFoundCode = "DeviceLibrary.DriverNotFound";
    public const string ImportFailedCode = "DeviceLibrary.ImportFailed";
    public const string AlreadyExistsCode = "DeviceLibrary.AlreadyExists";

    public static Error PackageNotFound(string packageId)
        => new(PackageNotFoundCode, $"驱动库中找不到驱动包「{packageId}」，可能已被移走，请重新扫描。");

    public static Error FingerprintChanged(string packageId)
        => new(FingerprintChangedCode, $"驱动包「{packageId}」的文件在确认前发生了变化，请重新扫描后再确认。");

    public static Error NotApprovable(string packageId, DriverPackageStatus status)
        => new(NotApprovableCode, $"驱动包「{packageId}」当前状态为 {status}，不能执行此操作。");

    public static Error ScanFailed(string detail)
        => new(ScanFailedCode, $"扫描驱动库失败：{detail}");

    public static Error DriverNotFound(DriverId driverId)
        => new(DriverNotFoundCode, $"驱动「{driverId}」不属于任何已载入的驱动包。");

    public static Error ImportFailed(string detail)
        => new(ImportFailedCode, $"导入驱动包失败：{detail}");

    public static Error AlreadyExists(string packageId, string version)
        => new(AlreadyExistsCode, $"驱动库中已有驱动包「{packageId}」{version} 的另一份内容。");
}
