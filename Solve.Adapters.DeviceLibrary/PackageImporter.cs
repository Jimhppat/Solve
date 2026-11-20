using Microsoft.Extensions.Logging;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 把一个外部目录（如驱动包的编译输出）复制进设备库，目标目录名为包标识。只复制文件，不执行任何代码。
/// </summary>
/// <remarks>
/// <para>原子性：先把扫描时读到的字节写进设备库下以 '.' 开头的临时目录（扫描会跳过它），校验指纹一致后再整体改名到位；
/// 替换已有版本时先把旧目录改名备份，新目录到位后才删除备份，中途失败则恢复旧目录。
/// 设备库里不会出现"复制了一半"的包。</para>
/// <para>写入的是扫描时读入内存的那份字节：导入后看到的指纹，就是这次扫描到的内容。</para>
/// </remarks>
internal static class PackageImporter
{
    public static Result Import(
        string root,
        string sourceDirectory,
        bool overwrite,
        IReadOnlyList<ScannedPackage> existing,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string source;
        try
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failed($"路径无效：{ex.Message}");
        }

        if (!Directory.Exists(source))
            return Failed($"目录不存在：{source}");

        var rootPath = Path.TrimEndingDirectorySeparator(root);
        // 驱动库根目录下的一级子目录就是驱动包本身，不需要导入；更深的目录（如与驱动库同根的源码的编译输出）可以导入
        var parent = Path.GetDirectoryName(source);
        if (parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), rootPath, StringComparison.OrdinalIgnoreCase))
            return Failed("所选目录已经是驱动库中的驱动包，不需要导入。");
        if (IsSameOrInside(rootPath, source))
            return Failed("所选目录包含驱动库本身，请选择驱动包自己的目录。");

        var scanned = PackageScanner.ScanPackage(source, cancellationToken);
        if (scanned.Manifest is not { } manifest)
            return Failed($"所选目录不是有效的驱动包：{string.Join("；", scanned.Problems)}");

        var id = manifest.Id;
        var other = existing.FirstOrDefault(p => p.Manifest?.Id == id && !string.Equals(p.DirectoryName, id, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
            return Failed($"驱动库中的目录「{other.DirectoryName}」已使用包标识 {id}，请先移走它。");

        var target = Path.Combine(rootPath, id);
        if (Directory.Exists(target))
        {
            var current = existing.FirstOrDefault(p => string.Equals(p.DirectoryName, id, StringComparison.OrdinalIgnoreCase));
            if (current is not null && current.Fingerprint == scanned.Fingerprint)
            {
                logger.LogInformation("导入驱动包 {PackageId}：驱动库中已是同一内容，未复制", id);
                return Result.Success();
            }

            if (!overwrite)
                return Result.Failure(DeviceLibraryErrors.AlreadyExists(id, current?.Manifest?.Version ?? "-"));
        }

        var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(rootPath, $"{PackageScanner.ReservedPrefix}import-{token}");
        var backup = Path.Combine(rootPath, $"{PackageScanner.ReservedPrefix}replaced-{token}");
        try
        {
            WriteFiles(staging, scanned.Files, cancellationToken);

            var written = PackageScanner.ScanPackage(staging, cancellationToken);
            if (written.Fingerprint != scanned.Fingerprint)
                return Failed("复制后的内容与源目录不一致（复制过程中源目录被修改？），已放弃导入。");

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    // 新目录没能到位：把旧版本放回去，设备库保持导入前的样子，再把原异常交给外层报告
                    Directory.Move(backup, target);
                    throw;
                }

                TryDelete(backup, logger);
            }
            else
            {
                Directory.Move(staging, target);
            }

            logger.LogWarning("导入驱动包 {PackageId} {Version}（来源 {Source}，指纹 {Fingerprint}），等待操作员信任",
                id, manifest.Version, source, scanned.Fingerprint);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "导入驱动包 {PackageId} 失败（来源 {Source}）", id, source);
            return Failed(ex.Message);
        }
        finally
        {
            if (Directory.Exists(staging))
                TryDelete(staging, logger);
        }
    }

    private static void WriteFiles(string staging, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(staging);
        foreach (var (relative, content) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 相对路径来自扫描（源目录内的真实文件），这里仍检查一次不会写到临时目录之外
            var path = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"包内文件路径越界：{relative}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }

    private static bool IsSameOrInside(string path, string directory)
        => string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string directory, ILogger logger)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留的临时目录以 '.' 开头，扫描会跳过，不影响设备库；记录下来便于人工清理
            logger.LogWarning(ex, "删除驱动库临时目录 {Directory} 失败，可手动删除", directory);
        }
    }

    private static Result Failed(string detail) => Result.Failure(DeviceLibraryErrors.ImportFailed(detail));
}
