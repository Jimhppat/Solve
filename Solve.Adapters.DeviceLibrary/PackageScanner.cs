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
/// 驱动库目录下的一个驱动源码项目（项目目录里同时有 package.json 与 *.csproj）。只读清单，不编译、不加载代码。
/// </summary>
/// <param name="RelativeDirectory">项目目录相对驱动库根目录的路径，'/' 分隔，如 "Chroma/Chroma.Drivers"</param>
/// <param name="ProjectFile">项目文件名，如 "Chroma.Drivers.csproj"</param>
/// <param name="Manifest">源码中的清单；无效时为 null，原因见 <paramref name="Problems"/></param>
internal sealed record SourceProject(
    string RelativeDirectory,
    string ProjectFile,
    PackageManifest? Manifest,
    IReadOnlyList<string> Problems);

/// <summary>
/// 一次扫描驱动库的结果：驱动包（根目录下的一级子目录）与驱动源码项目（源码目录中带清单的项目）
/// </summary>
internal sealed record LibraryScan(IReadOnlyList<ScannedPackage> Packages, IReadOnlyList<SourceProject> Sources)
{
    public static LibraryScan Empty { get; } = new([], []);
}

/// <summary>
/// 读取设备库目录：根目录下的每个一级子目录是一个驱动包；驱动源码目录不当作驱动包，只读出其中驱动项目的清单，
/// 用来报告"源码还没有编译出驱动包"。只读文件，不加载任何代码。
/// </summary>
/// <remarks>
/// 目录约定（与 Devicelibrary/Directory.Build.targets 一致）：
/// <code>
/// 驱动库根目录/
///   chroma.drivers/              ← 驱动包：根部有 package.json 与入口程序集，目录名建议等于包标识（编译时自动产出）
///   Chroma/                      ← 驱动源码（厂家目录）：下面有 *.csproj，扫描时不当作驱动包
///     Chroma.Drivers/            ← 驱动项目：*.csproj + package.json，扫描时核对是否已编译出同一包标识的驱动包
///     Chroma.Drivers.Tests/      ← 没有 package.json 的项目（测试等）不参与扫描
///   .import-xxxx/                ← '.' 开头：导入用的临时目录，跳过
/// </code>
/// </remarks>
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
    /// 在源码目录中向下查找项目文件的深度（0 为源码目录本身）：厂家目录 / 项目目录 / *.csproj 为 1，再多留一层余量
    /// </summary>
    private const int SourceProbeDepth = 2;

    /// <summary>
    /// 源码中清单文件的大小上限：清单只有几百字节，超过说明放错了文件，不读入内存
    /// </summary>
    private const long MaxSourceManifestBytes = 1024 * 1024;

    private const string ProjectFilePattern = "*.csproj";

    public static LibraryScan Scan(string rootDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootDirectory))
            return LibraryScan.Empty;

        var packages = new List<ScannedPackage>();
        var sources = new List<SourceProject>();
        foreach (var directory in Directory.EnumerateDirectories(rootDirectory)
                     .Where(d => !Path.GetFileName(d).StartsWith(ReservedPrefix))
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSourceDirectory(directory))
                sources.AddRange(FindSourceProjects(rootDirectory, directory, cancellationToken));
            else
                packages.Add(ScanPackage(directory, cancellationToken));
        }

        return new LibraryScan(packages, sources);
    }

    /// <summary>
    /// 驱动源码目录（驱动库与源码放在同一个根目录时，如 Devicelibrary/Chroma）：根部直接有项目文件，
    /// 或根部没有清单、下面几层内有项目文件。驱动包只含程序集与清单，绝不会带项目文件。
    /// 源码目录不当作驱动包读取（不读其中的 bin / obj，文件多、体积大），只由 <see cref="FindSourceProjects"/> 读出清单。
    /// </summary>
    internal static bool IsSourceDirectory(string directory)
    {
        try
        {
            if (Directory.EnumerateFiles(directory, ProjectFilePattern).Any())
                return true;
            if (File.Exists(Path.Combine(directory, SdkInfo.ManifestFileName)))
                return false;

            return EnumerateProjectFiles(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 判断不了就按驱动包处理：会显示为无效并写明原因，而不是悄悄消失
            return false;
        }
    }

    /// <summary>
    /// 源码目录中的驱动项目：同一目录里既有 *.csproj 又有 package.json。没有清单的项目（测试项目等）不是驱动项目，不报告。
    /// </summary>
    internal static IReadOnlyList<SourceProject> FindSourceProjects(string rootDirectory, string sourceDirectory, CancellationToken cancellationToken)
    {
        List<string> projectDirectories;
        try
        {
            projectDirectories = EnumerateProjectFiles(sourceDirectory)
                .Select(path => Path.GetDirectoryName(path)!)
                .Where(dir => !IsHidden(Path.GetRelativePath(rootDirectory, dir)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(dir => File.Exists(Path.Combine(dir, SdkInfo.ManifestFileName)))
                .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var relative = Path.GetRelativePath(rootDirectory, sourceDirectory).Replace('\\', '/');
            return [new SourceProject(relative, "-", null, [$"读取驱动源码目录失败：{ex.Message}"])];
        }

        var projects = new List<SourceProject>(projectDirectories.Count);
        foreach (var directory in projectDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(ReadSourceProject(rootDirectory, directory));
        }

        return projects;
    }

    private static SourceProject ReadSourceProject(string rootDirectory, string projectDirectory)
    {
        var relative = Path.GetRelativePath(rootDirectory, projectDirectory).Replace('\\', '/');
        string projectFile;
        byte[] manifestBytes;
        try
        {
            projectFile = Directory.EnumerateFiles(projectDirectory, ProjectFilePattern)
                .Select(Path.GetFileName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .First()!;

            var manifestPath = Path.Combine(projectDirectory, SdkInfo.ManifestFileName);
            if (new FileInfo(manifestPath).Length > MaxSourceManifestBytes)
                return new SourceProject(relative, projectFile, null, [$"{SdkInfo.ManifestFileName} 超过 {MaxSourceManifestBytes / 1024} KB，不像是驱动包清单。"]);

            manifestBytes = File.ReadAllBytes(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SourceProject(relative, "-", null, [$"读取驱动项目失败：{ex.Message}"]);
        }

        var manifest = PackageManifest.TryParse(manifestBytes, out var problems);
        return new SourceProject(relative, projectFile, manifest, problems);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string directory)
        => Directory.EnumerateFiles(directory, ProjectFilePattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = SourceProbeDepth,
            IgnoreInaccessible = true,
        });

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
