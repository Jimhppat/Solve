using Solve.Core.Domain.Common;

namespace Solve.Core.Application.DeviceLibrary;

/// <summary>
/// 驱动库目录的查看与保存（设置页「驱动库」卡片）。
/// </summary>
/// <remarks>
/// 驱动库在启动时扫描并载入，已载入的驱动无法卸载，所以改目录在下次启动时生效；
/// 本次运行始终使用 <see cref="CurrentDirectory"/>，与保存的设置可能不同。
/// </remarks>
public interface IDeviceLibraryLocation
{
    /// <summary>
    /// 本次运行正在使用的目录（完整路径）
    /// </summary>
    string CurrentDirectory { get; }

    /// <summary>
    /// 默认目录：从源码目录运行时为解决方案旁的 Devicelibrary，否则为程序目录下的 DeviceLibrary
    /// </summary>
    string DefaultDirectory { get; }

    /// <summary>
    /// 下次启动将使用的目录（已保存的设置；未保存过时等于默认目录）
    /// </summary>
    string NextDirectory { get; }

    /// <summary>
    /// 启动时保存的目录不可用、本次运行改用默认目录的原因；为 null 表示没有发生这种情况
    /// </summary>
    string? StartupFallbackReason { get; }

    /// <summary>
    /// 保存目录，下次启动生效；<paramref name="directory"/> 为 null 或空白表示恢复默认目录。
    /// 目录不存在时创建它（创建失败即保存失败）。涉及磁盘 I/O，在线程池上执行。
    /// </summary>
    /// <returns>规范化后的完整路径</returns>
    Task<Result<string>> SaveAsync(string? directory, CancellationToken cancellationToken);
}

/// <summary>
/// 驱动库目录相关错误码
/// </summary>
public static class DeviceLibraryLocationErrors
{
    public const string InvalidPathCode = "DeviceLibrary.InvalidPath";

    public static Error InvalidPath(string detail) => new(InvalidPathCode, $"驱动库目录不可用：{detail}");
}
