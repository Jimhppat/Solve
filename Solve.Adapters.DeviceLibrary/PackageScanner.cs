using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 扫描到的一个驱动包目录：清单（可能无效）、问题列表、文件指纹，以及计算指纹时读到的文件内容。
/// </summary>
/// <param name="Files">包内全部文件（相对路径，'/' 分隔，忽略大小写）→ 内容。加载程序集时直接用这份内容，
/// 而不是再读一次磁盘：确认的指纹与实际加载的字节必定一致，也不会锁住包文件。</param>
internal sealed record ScannedPackage(
    string DirectoryName,
    PackageManifest? Manifest,
    IReadOnlyList<string> Problems,
    string Fingerprint,
    IReadOnlyDictionary<string, byte[]> Files);

/// <summary>
/// 读取设备库目录下的每个子目录作为一个驱动包。只读文件，不加载任何代码。
/// </summary>
internal static class PackageScanner
{
    /// <summary>
    /// 单个包的文件数上限，防止误把大目录放进设备库
    /// </summary>
    public const int MaxFiles = 512;

    /// <summary>
    /// 单个包的总大小上限（字节）。驱动包只含程序集与清单，64 MB 远超正常需要
    /// </summary>
    public const long MaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 以 '.' 开头的目录与文件不属于驱动包：根目录下是导入时的临时目录；包内是 .gitignore 之类的辅助文件，
    /// 既不读取、不计入指纹，也不能被当作程序集载入
    /// </summary>
    public const char ReservedPrefix = '.';

    /// <summary>
    /// 判断"根目录下没有清单的子目录是不是驱动源码"时向下查找项目文件的深度：厂家目录 / 项目目录 / *.csproj
    /// </summary>
    private const int SourceProbeDepth = 2;

    public static IReadOnlyList<ScannedPackage> Scan(string rootDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootDirectory))
            return [];

        return Directory.EnumerateDirectories(rootDirectory)
            .Where(d => !Path.GetFileName(d).StartsWith(ReservedPrefix))
            .Where(d => !IsSourceDirectory(d))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => ScanPackage(d, cancellationToken))
            .ToList();
    }

    /// <summary>
    /// 驱动源码目录（驱动库与源码放在同一个根目录时，如 Devicelibrary/Chroma）：根部没有清单、下面有项目文件。
    /// 跳过它们，既不报"无效"，也不去读其中的 bin / obj（文件多、体积大）。根部有清单的一律按驱动包处理。
    /// </summary>
    internal static bool IsSourceDirectory(string directory)
    {
        if (File.Exists(Path.Combine(directory, SdkInfo.ManifestFileName)))
            return false;

        try
        {
            return Directory.EnumerateFiles(directory, "*.csproj", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = SourceProbeDepth,
                IgnoreInaccessible = true,
            }).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 判断不了就按驱动包处理：会显示为无效并写明原因，而不是悄悄消失
            return false;
        }
    }

    /// <summary>
    /// 把一个目录作为驱动包读取（目录名即包所在目录名）。只读文件，不加载任何代码。
    /// </summary>
    public static ScannedPackage ScanPackage(string directory, CancellationToken cancellationToken = default)
    {
        var directoryName = Path.GetFileName(directory);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !IsHidden(Path.GetRelativePath(directory, path)))
                .ToList();
            if (paths.Count > MaxFiles)
                return Invalid(directoryName, $"包内文件超过 {MaxFiles} 个。");

            long total = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += new FileInfo(path).Length;
                if (total > MaxTotalBytes)
                    return Invalid(directoryName, $"包的总大小超过 {MaxTotalBytes / 1024 / 1024} MB。");

                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                files[relative] = File.ReadAllBytes(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid(directoryName, $"读取包文件失败：{ex.Message}");
        }

        var fingerprint = Fingerprint(files);

        if (!files.TryGetValue(SdkInfo.ManifestFileName, out var manifestBytes))
            return new ScannedPackage(directoryName, null, [$"缺少清单文件 {SdkInfo.ManifestFileName}。"], fingerprint, files);

        var manifest = PackageManifest.TryParse(manifestBytes, out var problems);
        if (manifest is null)
            return new ScannedPackage(directoryName, null, problems, fingerprint, files);

        if (!files.ContainsKey(manifest.EntryAssembly))
            return new ScannedPackage(directoryName, null, [$"找不到入口程序集 {manifest.EntryAssembly}。"], fingerprint, files);

        return new ScannedPackage(directoryName, manifest, [], fingerprint, files);
    }

    /// <summary>
    /// 相对路径中任一段以 '.' 开头（隐藏文件或位于隐藏目录中）
    /// </summary>
    private static bool IsHidden(string relativePath)
        => relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.StartsWith(ReservedPrefix));

    private static ScannedPackage Invalid(string directoryName, string problem)
        => new(directoryName, null, [problem], string.Empty, new Dictionary<string, byte[]>());

    /// <summary>
    /// 包指纹：按相对路径排序，依次对"路径 + 长度 + 内容哈希"做 SHA-256。增删、改名、改动任何一个文件都会改变指纹。
    /// </summary>
    internal static string Fingerprint(IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            hash.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(length, content.LongLength);
            hash.AppendData(length);
            hash.AppendData(SHA256.HashData(content));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
