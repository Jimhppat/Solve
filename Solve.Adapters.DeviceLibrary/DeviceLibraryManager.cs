using System.Reflection;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 设备库：启动时扫描设备库目录，只加载经操作员确认且指纹未变的驱动包，合并成驱动目录供内核使用。
/// </summary>
/// <remarks>
/// <para>信任策略：确认记录保存在设置库（键 devicelibrary.approved.&lt;包标识&gt;，值为确认时的指纹）。
/// 包第一次出现或任一文件变化都需要重新确认；撤销即把记录清空。确认与撤销都在下次启动时生效。</para>
/// <para>隔离：每个包一个加载上下文，只共享 SDK；包内代码在加载期抛出的任何异常都只让该包加载失败，不影响程序启动。
/// 驱动标识与其他包或内置驱动重复时，涉及的包都不加载，绝不悄悄挑一个。</para>
/// <para>线程：<see cref="Packages"/> 与 <see cref="Catalog"/> 可被任意线程读取；扫描、确认、撤销互斥执行。</para>
/// </remarks>
public sealed class DeviceLibraryManager : IDeviceLibrary
{
    public const string ApprovalKeyPrefix = "devicelibrary.approved.";

    private static readonly string SdkAssemblyName = typeof(IDriverPackage).Assembly.GetName().Name!;

    private readonly IAppSettingsStore _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 本次运行启动时的结果（按包标识）：加载过的包及其指纹，重新扫描时据此判断"仍在使用 / 已改动 / 已撤销"
    private readonly Dictionary<string, DriverPackageInfo> _startupResults;

    private IReadOnlyList<DriverPackageInfo> _packages;

    private DeviceLibraryManager(
        string rootDirectory,
        IAppSettingsStore settings,
        ILogger logger,
        DriverCatalog catalog,
        IReadOnlyDictionary<DriverId, IDeviceDriver> drivers,
        IReadOnlyList<DriverPackageInfo> packages)
    {
        RootDirectory = rootDirectory;
        _settings = settings;
        _logger = logger;
        Catalog = catalog;
        Drivers = drivers;
        _packages = packages;
        // 包标识重复的几个目录同属冲突状态，取其一即可（重新扫描时只用它判断"本次运行是否在用"）
        _startupResults = packages
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public string RootDirectory { get; }

    /// <summary>
    /// 内置驱动 + 本次启动加载的驱动；本次运行内不变
    /// </summary>
    public DriverCatalog Catalog { get; }

    /// <summary>
    /// 本次启动加载的第三方驱动实现（按驱动标识），供协议层创建会话
    /// </summary>
    public IReadOnlyDictionary<DriverId, IDeviceDriver> Drivers { get; }

    public IReadOnlyList<DriverPackageInfo> Packages => Volatile.Read(ref _packages);

    /// <summary>
    /// 启动时调用一次：扫描、按确认记录加载、合并驱动目录。设备库目录不存在时创建它（空库）。
    /// </summary>
    public static async Task<DeviceLibraryManager> LoadAsync(
        string rootDirectory,
        IAppSettingsStore settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var root = Path.GetFullPath(rootDirectory);
        var scanned = ScanOrEmpty(root, logger);
        var approvals = await ReadApprovalsAsync(scanned, settings, logger, cancellationToken).ConfigureAwait(false);
        var duplicates = DuplicateIds(scanned);

        var results = new Dictionary<string, DriverPackageInfo>(StringComparer.Ordinal);
        var loaded = new List<LoadedPackage>();
        foreach (var package in scanned)
        {
            var info = Evaluate(package, approvals, duplicates);
            if (info.Status == DriverPackageStatus.Loaded)
            {
                var load = TryLoad(package, logger);
                if (load.Package is { } ok)
                    loaded.Add(ok);
                else
                    info = info with { Status = load.Status, Problem = load.Problem };
            }

            results[Key(package, duplicates)] = info;
        }

        // 驱动标识冲突（包与包、包与内置驱动）：涉及的包整体不加载
        var conflicts = DriverConflicts(loaded);
        var drivers = new Dictionary<DriverId, IDeviceDriver>();
        var descriptors = new List<DriverDescriptor>(DriverCatalog.BuiltIns);
        foreach (var package in loaded)
        {
            var key = package.Scanned.Manifest!.Id;
            if (conflicts.TryGetValue(key, out var conflict))
            {
                results[key] = results[key] with { Status = DriverPackageStatus.Conflict, Problem = conflict };
                continue;
            }

            foreach (var driver in package.Drivers)
            {
                drivers[driver.Descriptor.Id] = driver.Implementation;
                descriptors.Add(driver.Descriptor);
            }

            results[key] = results[key] with { DriverNames = package.Drivers.Select(d => d.Descriptor.Name).ToList() };
        }

        var packages = results.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        foreach (var package in packages)
            LogPackage(logger, package);

        logger.LogInformation("设备库 {Root}：驱动包 {Total} 个，已加载 {Loaded} 个，待确认 {Pending} 个，第三方驱动 {Drivers} 个",
            root, packages.Count, packages.Count(p => p.Status == DriverPackageStatus.Loaded),
            packages.Count(p => p.Status == DriverPackageStatus.PendingApproval), drivers.Count);

        return new DeviceLibraryManager(root, settings, logger, new DriverCatalog(descriptors), drivers, packages);
    }

    public async Task<Result<IReadOnlyList<DriverPackageInfo>>> RescanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Result<IReadOnlyList<DriverPackageInfo>>.Success(await RescanCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<IReadOnlyList<DriverPackageInfo>>> ApproveAsync(
        string packageId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(fingerprint);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 先按磁盘现状重新扫描：确认的必须是此刻磁盘上的那份内容
            var current = await RescanCoreAsync(cancellationToken).ConfigureAwait(false);
            var package = current.FirstOrDefault(p => p.Id == packageId);
            if (package is null)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.PackageNotFound(packageId));
            if (!string.Equals(package.Fingerprint, fingerprint, StringComparison.Ordinal))
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.FingerprintChanged(packageId));
            if (!package.CanApprove)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.NotApprovable(packageId, package.Status));

            var saved = await _settings.SetAsync(ApprovalKeyPrefix + packageId, fingerprint, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(saved.Error);

            _logger.LogWarning("操作员确认驱动包 {PackageId} {Name} {Version}（作者 {Author}，指纹 {Fingerprint}），重启后加载",
                package.Id, package.Name, package.Version, package.Author, fingerprint);
            return Result<IReadOnlyList<DriverPackageInfo>>.Success(await RescanCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<IReadOnlyList<DriverPackageInfo>>> RevokeAsync(string packageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var package = Packages.FirstOrDefault(p => p.Id == packageId);
            if (package is null)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.PackageNotFound(packageId));
            if (!package.CanRevoke)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.NotApprovable(packageId, package.Status));

            var saved = await _settings.SetAsync(ApprovalKeyPrefix + packageId, string.Empty, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(saved.Error);

            _logger.LogWarning("操作员撤销驱动包 {PackageId} {Name} {Version} 的确认，重启后不再加载", package.Id, package.Name, package.Version);
            return Result<IReadOnlyList<DriverPackageInfo>>.Success(await RescanCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    // ==================== 扫描与判定 ====================

    /// <summary>
    /// 不加载任何代码，只按磁盘现状、确认记录与本次启动的结果给出每个包的状态
    /// </summary>
    private async Task<IReadOnlyList<DriverPackageInfo>> RescanCoreAsync(CancellationToken cancellationToken)
    {
        var scanned = ScanOrEmpty(RootDirectory, _logger);
        var approvals = await ReadApprovalsAsync(scanned, _settings, _logger, cancellationToken).ConfigureAwait(false);
        var duplicates = DuplicateIds(scanned);

        var results = new Dictionary<string, DriverPackageInfo>(StringComparer.Ordinal);
        foreach (var package in scanned)
        {
            var info = Evaluate(package, approvals, duplicates);
            _startupResults.TryGetValue(info.Id, out var startup);

            if (startup?.Status == DriverPackageStatus.Loaded)
            {
                // 本次运行正在使用启动时加载的版本
                info = info with { DriverNames = startup.DriverNames };
                var approvedSame = approvals.TryGetValue(info.Id, out var approval)
                                   && string.Equals(approval, startup.Fingerprint, StringComparison.Ordinal);
                if (info.Fingerprint != startup.Fingerprint)
                {
                    info = info.Status == DriverPackageStatus.Loaded
                        ? info with { Status = DriverPackageStatus.ApprovedRestartRequired, Problem = "已确认新版本，重启后替换本次运行使用的版本。" }
                        : info with { Problem = "包文件已改动，本次运行仍使用启动时加载的版本。" };
                }
                else if (!approvedSame)
                {
                    info = info with { Status = DriverPackageStatus.RevokedRestartRequired, Problem = null };
                }
            }
            else if (info.Status == DriverPackageStatus.Loaded)
            {
                // 已确认但本次运行未加载：启动时已确认、尝试加载却失败或冲突的同一版本，结果不变；其余是刚确认的，重启后加载
                var failedAtStartup = startup is not null
                                      && startup.Fingerprint == info.Fingerprint
                                      && startup.Status is DriverPackageStatus.LoadFailed
                                          or DriverPackageStatus.Conflict
                                          or DriverPackageStatus.Incompatible;
                info = failedAtStartup
                    ? info with { Status = startup!.Status, Problem = startup.Problem }
                    : info with { Status = DriverPackageStatus.ApprovedRestartRequired };
            }

            results[Key(package, duplicates)] = info;
        }

        // 已加载的包目录被移走：本次运行仍在使用，重启后不再加载
        foreach (var startup in _startupResults.Values.Where(p => p.Status == DriverPackageStatus.Loaded))
        {
            if (!results.ContainsKey(startup.Id))
                results[startup.Id] = startup with { Status = DriverPackageStatus.RevokedRestartRequired, Problem = "包目录已移除，重启后不再加载。" };
        }

        var packages = results.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        Volatile.Write(ref _packages, packages);
        return packages;
    }

    /// <summary>
    /// 静态判定（不加载代码）：清单、重复、SDK 版本、确认记录。确认且指纹一致时返回 <see cref="DriverPackageStatus.Loaded"/>，表示"应当加载"。
    /// </summary>
    private static DriverPackageInfo Evaluate(
        ScannedPackage package,
        IReadOnlyDictionary<string, string> approvals,
        IReadOnlySet<string> duplicates)
    {
        if (package.Manifest is not { } manifest)
        {
            return new DriverPackageInfo(package.DirectoryName, package.DirectoryName, package.DirectoryName, "-", "-",
                package.Fingerprint, DriverPackageStatus.Invalid, string.Join("；", package.Problems), []);
        }

        var info = new DriverPackageInfo(manifest.Id, package.DirectoryName, manifest.Name, manifest.Version, manifest.Author,
            package.Fingerprint, DriverPackageStatus.PendingApproval, null, []);

        if (duplicates.Contains(manifest.Id))
            return info with { Status = DriverPackageStatus.Conflict, Problem = $"多个目录使用了同一个包标识 {manifest.Id}。" };

        if (manifest.SdkVersion != SdkInfo.MajorVersion)
        {
            return info with
            {
                Status = DriverPackageStatus.Incompatible,
                Problem = $"驱动包需要 SDK {manifest.SdkVersion}，本程序提供 SDK {SdkInfo.MajorVersion}。",
            };
        }

        return approvals.TryGetValue(manifest.Id, out var approved)
               && string.Equals(approved, package.Fingerprint, StringComparison.Ordinal)
            ? info with { Status = DriverPackageStatus.Loaded }
            : info;
    }

    // 结果按包标识区分；清单无效或包标识重复时再带上目录名，避免几个目录相互覆盖
    private static string Key(ScannedPackage package, IReadOnlySet<string> duplicates)
        => package.Manifest is { } manifest && !duplicates.Contains(manifest.Id)
            ? manifest.Id
            : $"{package.Manifest?.Id ?? package.DirectoryName}@{package.DirectoryName}";

    private static HashSet<string> DuplicateIds(IReadOnlyList<ScannedPackage> scanned)
        => scanned
            .Where(p => p.Manifest is not null)
            .GroupBy(p => p.Manifest!.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<ScannedPackage> ScanOrEmpty(string root, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(root);
            return PackageScanner.Scan(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogError(ex, "无法读取设备库目录 {Root}，本次没有第三方驱动", root);
            return [];
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadApprovalsAsync(
        IReadOnlyList<ScannedPackage> scanned,
        IAppSettingsStore settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var approvals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in scanned.Where(p => p.Manifest is not null).Select(p => p.Manifest!.Id).Distinct(StringComparer.Ordinal))
        {
            var read = await settings.GetAsync(ApprovalKeyPrefix + id, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure)
            {
                // 读不到确认记录按未确认处理：宁可少加载一个驱动，也不加载未经确认的代码
                logger.LogError("读取驱动包 {PackageId} 的确认记录失败，按未确认处理：{Error}", id, read.Error);
                continue;
            }

            if (!string.IsNullOrEmpty(read.Value))
                approvals[id] = read.Value;
        }

        return approvals;
    }

    // ==================== 加载 ====================

    private sealed record LoadedDriver(DriverDescriptor Descriptor, IDeviceDriver Implementation);

    private sealed record LoadedPackage(ScannedPackage Scanned, IReadOnlyList<LoadedDriver> Drivers);

    private readonly record struct LoadOutcome(LoadedPackage? Package, DriverPackageStatus Status, string? Problem)
    {
        public static LoadOutcome Failed(string problem) => new(null, DriverPackageStatus.LoadFailed, problem);
    }

    /// <summary>
    /// 在独立上下文中加载包并读取驱动说明。这里是执行第三方代码的边界：包内任何异常都只让本包加载失败。
    /// </summary>
    private static LoadOutcome TryLoad(ScannedPackage package, ILogger logger)
    {
        var manifest = package.Manifest!;
        try
        {
            var context = new PackageLoadContext(manifest.Id, package.Files);
            var entry = context.LoadPackageAssembly(manifest.EntryAssembly);

            var sdk = entry.GetReferencedAssemblies().FirstOrDefault(a => a.Name == SdkAssemblyName);
            if (sdk is null)
                return LoadOutcome.Failed($"入口程序集没有引用 {SdkAssemblyName}。");
            if (sdk.Version?.Major != SdkInfo.MajorVersion)
            {
                return new LoadOutcome(null, DriverPackageStatus.Incompatible,
                    $"入口程序集按 SDK {sdk.Version} 编译，本程序提供 SDK {SdkInfo.MajorVersion}。");
            }

            var type = entry.GetType(manifest.EntryType, throwOnError: false);
            if (type is null || !type.IsPublic || type.IsAbstract || !typeof(IDriverPackage).IsAssignableFrom(type))
                return LoadOutcome.Failed($"入口类型 {manifest.EntryType} 不存在、不是公开类型或没有实现 IDriverPackage。");
            if (type.GetConstructor(Type.EmptyTypes) is null)
                return LoadOutcome.Failed($"入口类型 {manifest.EntryType} 没有公开的无参构造函数。");

            var instance = (IDriverPackage)Activator.CreateInstance(type)!;
            var implementations = instance.Drivers?.ToList();
            if (implementations is null || implementations.Count == 0)
                return LoadOutcome.Failed("驱动包没有提供任何驱动。");

            var drivers = new List<LoadedDriver>(implementations.Count);
            var problems = new List<string>();
            foreach (var implementation in implementations)
            {
                if (ToDescriptor(implementation?.Info, out var problem) is { } descriptor)
                {
                    if (drivers.Exists(d => d.Descriptor.Id == descriptor.Id))
                        problems.Add($"包内驱动标识重复：{descriptor.Id}。");
                    else
                        drivers.Add(new LoadedDriver(descriptor, implementation!));
                }
                else
                {
                    problems.Add(problem!);
                }
            }

            return problems.Count == 0
                ? new LoadOutcome(new LoadedPackage(package, drivers), DriverPackageStatus.Loaded, null)
                : LoadOutcome.Failed(string.Join("；", problems));
        }
        catch (Exception ex)
        {
            var root = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            logger.LogError(root, "驱动包 {PackageId} 加载失败", manifest.Id);
            return LoadOutcome.Failed($"{root.GetType().Name}：{root.Message}");
        }
    }

    private static DriverDescriptor? ToDescriptor(DriverInfo? info, out string? problem)
    {
        problem = null;
        if (info is null)
        {
            problem = "驱动说明为空。";
            return null;
        }

        if (!DriverId.TryParse(info.Id, out var id))
        {
            problem = $"驱动标识「{info.Id}」格式无效。";
            return null;
        }

        HardwareCategory? category = info.Category switch
        {
            DeviceCategory.General => HardwareCategory.General,
            DeviceCategory.PowerAndLoad => HardwareCategory.PowerAndLoad,
            _ => null,
        };
        if (category is null)
        {
            problem = $"驱动 {info.Id} 的分类未定义：{(int)info.Category}。";
            return null;
        }

        var descriptor = new DriverDescriptor(id, info.Name?.Trim() ?? string.Empty, category.Value,
            info.Description?.Trim() ?? string.Empty, info.MinAddress, info.MaxAddress);
        try
        {
            descriptor.EnsureValid();
            return descriptor;
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 包标识 → 冲突原因。与内置驱动重复只淘汰该包；两个包重复则两个都淘汰。
    /// </summary>
    private static Dictionary<string, string> DriverConflicts(IReadOnlyList<LoadedPackage> loaded)
    {
        var conflicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var builtIns = DriverCatalog.BuiltIns.Select(d => d.Id).ToHashSet();
        var owners = new Dictionary<DriverId, string>();

        foreach (var package in loaded)
        {
            var packageId = package.Scanned.Manifest!.Id;
            foreach (var id in package.Drivers.Select(d => d.Descriptor.Id))
            {
                if (builtIns.Contains(id))
                {
                    conflicts[packageId] = $"驱动标识 {id} 与程序内置驱动重复。";
                }
                else if (owners.TryGetValue(id, out var owner))
                {
                    conflicts[packageId] = $"驱动标识 {id} 与驱动包 {owner} 重复。";
                    conflicts.TryAdd(owner, $"驱动标识 {id} 与驱动包 {packageId} 重复。");
                }
                else
                {
                    owners[id] = packageId;
                }
            }
        }

        return conflicts;
    }

    private static void LogPackage(ILogger logger, DriverPackageInfo package)
    {
        if (package.Status == DriverPackageStatus.Loaded)
        {
            logger.LogInformation("驱动包 {PackageId} {Name} {Version}（作者 {Author}）已加载，驱动：{Drivers}",
                package.Id, package.Name, package.Version, package.Author, string.Join("、", package.DriverNames));
        }
        else
        {
            logger.LogWarning("驱动包 {PackageId} {Version}（目录 {Directory}，指纹 {Fingerprint}）未加载：{Status}，{Reason}",
                package.Id, package.Version, package.Directory, package.Fingerprint, package.Status,
                package.Problem ?? (package.Status == DriverPackageStatus.PendingApproval ? "等待操作员在设置页确认" : "-"));
        }
    }
}
