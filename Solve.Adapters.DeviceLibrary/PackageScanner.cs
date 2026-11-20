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
    /// 设备库根目录下以 '.' 开头的子目录是导入时的临时目录，不作为驱动包
    /// </summary>
    public const char ReservedPrefix = '.';

    public static IReadOnlyList<ScannedPackage> Scan(string rootDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootDirectory))
            return [];

        return Directory.EnumerateDirectories(rootDirectory)
            .Where(d => !Path.GetFileName(d).StartsWith(ReservedPrefix))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => ScanPackage(d, cancellationToken))
            .ToList();
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
            var paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
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
