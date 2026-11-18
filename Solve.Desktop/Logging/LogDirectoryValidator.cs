using System;
using System.IO;
using System.Security;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Logging;

/// <summary>
/// 日志目录的校验与准备：切换前、启动读取保存的目录时都要过这一关。
/// </summary>
public static class LogDirectoryValidator
{
    private const string ProbeFilePrefix = ".solve-write-probe-";

    /// <summary>
    /// 确认 <paramref name="directory"/> 可以作为日志目录：必须是本机磁盘上的完整路径，不存在时创建，
    /// 并实际写入一个探测文件确认可写。成功时返回规范化后的完整路径。
    /// </summary>
    /// <remarks>
    /// 不接受网络路径（UNC 与映射的网络驱动器）：网络中断时文件输出只会把错误写进自诊断文件，黑匣子会悄悄断档。
    /// 只检查目录本身是否可用，不检查剩余空间：磁盘写满由文件输出的自诊断记录。
    /// </remarks>
    public static Result<string> Prepare(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var trimmed = directory.Trim();
        if (trimmed.Length == 0)
            return Result<string>.Failure(LogDirectoryErrors.InvalidPath("目录不能为空"));

        if (!Path.IsPathFullyQualified(trimmed))
            return Result<string>.Failure(LogDirectoryErrors.InvalidPath($"请填写完整路径（含盘符），如 D:\\SolveLogs：{trimmed}"));

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Failure(LogDirectoryErrors.InvalidPath($"路径格式无效：{trimmed}（{ex.Message}）"));
        }

        // \\server\share 与 \\?\、\\.\ 设备路径一律拒绝
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return Result<string>.Failure(LogDirectoryErrors.NetworkPath(fullPath));

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
            if (drive.DriveType == DriveType.Network)
                return Result<string>.Failure(LogDirectoryErrors.NetworkPath(fullPath));
            if (drive.DriveType == DriveType.NoRootDirectory)
                return Result<string>.Failure(LogDirectoryErrors.InvalidPath($"驱动器 {drive.Name} 不存在：{fullPath}"));

            Directory.CreateDirectory(fullPath);

            // DeleteOnClose：探测文件随句柄关闭删除，进程中途崩溃也不会留在目录里
            var probe = Path.Combine(fullPath, ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
            using var stream = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            return Result<string>.Failure(LogDirectoryErrors.NotWritable(fullPath, ex.Message));
        }

        return Result<string>.Success(fullPath);
    }

    /// <summary>
    /// 两个完整路径是否指向同一目录。Windows 路径不区分大小写，结尾分隔符不影响比较。
    /// </summary>
    public static bool SameDirectory(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
