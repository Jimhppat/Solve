using System.Reflection;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Drivers;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 设备库：扫描设备库目录、导入驱动包、按操作员的信任载入驱动包、启停单个驱动，合并成运行中的驱动目录供内核使用。
/// </summary>
/// <remarks>
/// <para>信任策略：扫描只读清单与文件（清单声明了包内驱动，不执行代码就能列出）。信任记录保存在设置库
/// （键 devicelibrary.approved.&lt;包标识&gt;，值为信任时的指纹）；包第一次出现或任一文件变化都需要重新信任。
/// 信任后立即在独立加载上下文中载入，并核对代码中的驱动说明与清单声明一致、每个驱动都带仿真设备（SDK 1.4）。</para>
/// <para>无法卸载：.NET 不能安全卸载仍被引用的程序集，所以已载入的包在本次运行中不会被替换：
/// 磁盘上换成新版本时，新版本需要重启生效；撤销信任与停用驱动只把驱动从驱动表移除，代码留在内存中但不再被调用。</para>
/// <para>隔离：每个包一个加载上下文，只共享 SDK；包内代码在载入期抛出的任何异常都只让该包载入失败。
/// 驱动标识与其他包或内置驱动重复时不载入，绝不悄悄挑一个；清单声明在载入前就能发现这种冲突。</para>
/// <para>线程：<see cref="Packages"/> 与 <see cref="Catalog"/> 可被任意线程读取；其余操作互斥执行，内部状态只在互斥区内读写。</para>
/// </remarks>
public sealed class DeviceLibraryManager : IDeviceLibrary
{
    public const string ApprovalKeyPrefix = "devicelibrary.approved.";
    public const string IgnoredKeyPrefix = "devicelibrary.ignored.";
    public const string DisabledDriverKeyPrefix = "devicelibrary.driver-disabled.";

    private const string DisabledValue = "true";

    private static readonly string SdkAssemblyName = typeof(IDriverPackage).Assembly.GetName().Name!;

    private readonly IAppSettingsStore _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LiveDriverCatalog _catalog = new(new DriverCatalog());

    // ---- 以下状态只在 _gate 内读写 ----

    // 本次运行已载入的包（代码已在内存中），按包标识
    private readonly Dictionary<string, LoadedPackage> _loaded = new(StringComparer.Ordinal);

    // 已载入且仍受信任的包：它们的驱动（未停用的）在驱动表中
    private readonly HashSet<string> _trusted = new(StringComparer.Ordinal);

    // 本次运行中已判定不能载入的包（载入失败、启动时的驱动标识冲突），同一指纹不再重复尝试
    private readonly Dictionary<string, Failure> _failures = new(StringComparer.Ordinal);

    // 操作员停用的驱动
    private readonly HashSet<DriverId> _disabled = [];

    private IReadOnlyList<DriverPackageInfo> _packages = [];

    private DeviceLibraryManager(string rootDirectory, IAppSettingsStore settings, ILogger logger)
    {
        RootDirectory = rootDirectory;
        _settings = settings;
        _logger = logger;
    }

    public string RootDirectory { get; }

    /// <summary>
    /// 运行中的驱动目录：内置驱动 + 已信任、已载入、未停用的驱动。变化时触发 <see cref="IDriverCatalog.Changed"/>
    /// </summary>
    public IDriverCatalog Catalog => _catalog;

    /// <summary>
    /// 当前驱动目录中的第三方驱动实现（按驱动标识）
    /// </summary>
    public IReadOnlyDictionary<DriverId, IDeviceDriver> Drivers => _catalog.Current.Implementations;

    public IReadOnlyList<DriverPackageInfo> Packages => Volatile.Read(ref _packages);

    /// <summary>
    /// 启动时调用一次：扫描、载入已信任的包、合并驱动目录。设备库目录不存在时创建它（空库）。
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

        var manager = new DeviceLibraryManager(Path.GetFullPath(rootDirectory), settings, logger);
        await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return manager;
    }

    public Task<Result<IReadOnlyList<DriverPackageInfo>>> RescanAsync(CancellationToken cancellationToken)
        => ExclusiveAsync(async () =>
        {
            var (_, packages) = await RescanCoreAsync(cancellationToken).ConfigureAwait(false);
            LogSummary("手动扫描", packages);
            return Result<IReadOnlyList<DriverPackageInfo>>.Success(packages);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<DriverPackageInfo>>> ApproveAsync(string packageId, string fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(fingerprint);

        return ExclusiveAsync(async () =>
        {
            // 先按磁盘现状重新扫描：信任的必须是此刻磁盘上的那份内容，载入的也正是这次读到的字节
            var (scanned, current) = await RescanCoreAsync(cancellationToken).ConfigureAwait(false);
            if (Find(current, packageId, fingerprint, p => p.CanApprove) is { } refused)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(refused);

            var saved = await _settings.SetAsync(ApprovalKeyPrefix + packageId, fingerprint, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(saved.Error);

            _logger.LogWarning("操作员信任驱动包 {PackageId}（指纹 {Fingerprint}）", packageId, fingerprint);

            if (_loaded.TryGetValue(packageId, out var loaded))
            {
                // 同一版本本次运行已载入（之前撤销过）：恢复信任即可，不再载入；不同版本只能重启后替换
                if (loaded.Fingerprint == fingerprint)
                {
                    _trusted.Add(packageId);
                    RebuildCatalog();
                }
            }
            else if (scanned.FirstOrDefault(p => p.Manifest?.Id == packageId && p.Fingerprint == fingerprint) is { } package)
            {
                await LoadAndTrustAsync(package, cancellationToken).ConfigureAwait(false);
                RebuildCatalog();
            }

            return Result<IReadOnlyList<DriverPackageInfo>>.Success((await RescanCoreAsync(cancellationToken).ConfigureAwait(false)).Packages);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DriverPackageInfo>>> RevokeAsync(string packageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageId);

        return ExclusiveAsync(async () =>
        {
            var package = Packages.FirstOrDefault(p => p.Id == packageId && !p.IsSource);
            if (package is null)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.PackageNotFound(packageId));
            if (!package.CanRevoke)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.NotApprovable(packageId, package.Status));

            var saved = await _settings.SetAsync(ApprovalKeyPrefix + packageId, string.Empty, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(saved.Error);

            if (_trusted.Remove(packageId))
                RebuildCatalog();

            _logger.LogWarning("操作员撤销驱动包 {PackageId} {Name} {Version} 的信任，其驱动已从驱动表移除", package.Id, package.Name, package.Version);
            return Result<IReadOnlyList<DriverPackageInfo>>.Success((await RescanCoreAsync(cancellationToken).ConfigureAwait(false)).Packages);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DriverPackageInfo>>> IgnoreAsync(string packageId, string fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(fingerprint);

        return ExclusiveAsync(async () =>
        {
            var (_, current) = await RescanCoreAsync(cancellationToken).ConfigureAwait(false);
            if (Find(current, packageId, fingerprint, p => p.CanIgnore) is { } refused)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(refused);

            var saved = await _settings.SetAsync(IgnoredKeyPrefix + packageId, fingerprint, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(saved.Error);

            _logger.LogInformation("操作员忽略驱动包 {PackageId}（指纹 {Fingerprint}）", packageId, fingerprint);
            return Result<IReadOnlyList<DriverPackageInfo>>.Success((await RescanCoreAsync(cancellationToken).ConfigureAwait(false)).Packages);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DriverPackageInfo>>> SetDriverEnabledAsync(DriverId driverId, bool enabled, CancellationToken cancellationToken)
        => ExclusiveAsync(async () =>
        {
            var owner = _loaded.Values.FirstOrDefault(p => p.Drivers.Any(d => d.Descriptor.Id == driverId));
            if (owner is null)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.DriverNotFound(driverId));

            var saved = await _settings.SetAsync(DisabledDriverKeyPrefix + driverId, enabled ? string.Empty : DisabledValue, cancellationToken)
                .ConfigureAwait(false);
            if (saved.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(saved.Error);

            var changed = enabled ? _disabled.Remove(driverId) : _disabled.Add(driverId);
            if (changed)
                RebuildCatalog();

            _logger.LogWarning("操作员{Action}驱动 {DriverId}（驱动包 {PackageId}）", enabled ? "启用" : "停用", driverId, owner.Id);
            return Result<IReadOnlyList<DriverPackageInfo>>.Success((await RescanCoreAsync(cancellationToken).ConfigureAwait(false)).Packages);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<DriverPackageInfo>>> ImportAsync(string sourceDirectory, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);

        return ExclusiveAsync(async () =>
        {
            var existing = ScanOrEmpty(RootDirectory, _logger, cancellationToken).Packages;
            var imported = PackageImporter.Import(RootDirectory, sourceDirectory, overwrite, existing, _logger, cancellationToken);
            if (imported.IsFailure)
                return Result<IReadOnlyList<DriverPackageInfo>>.Failure(imported.Error);

            return Result<IReadOnlyList<DriverPackageInfo>>.Success((await RescanCoreAsync(cancellationToken).ConfigureAwait(false)).Packages);
        }, cancellationToken);
    }

    // ==================== 启动 ====================

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var scan = ScanOrEmpty(RootDirectory, _logger, cancellationToken);
        var scanned = scan.Packages;
        var records = await ReadRecordsAsync(scanned, cancellationToken).ConfigureAwait(false);

        // 已信任且指纹一致的包是载入候选；候选之间（以及与内置驱动）声明的驱动标识重复时，涉及的包都不载入
        var candidates = scanned
            .Where(p => p.Manifest is { } m
                        && SdkCompatible(m)
                        && records.Approvals.TryGetValue(m.Id, out var approved)
                        && approved == p.Fingerprint)
            .GroupBy(p => p.Manifest!.Id, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .ToList();

        foreach (var (packageId, reason) in StartupConflicts(candidates))
        {
            var package = candidates.Single(p => p.Manifest!.Id == packageId);
            _failures[packageId] = new Failure(package.Fingerprint, DriverPackageStatus.Conflict, reason);
        }

        foreach (var package in candidates.Where(p => !_failures.ContainsKey(p.Manifest!.Id)))
            await LoadAndTrustAsync(package, cancellationToken).ConfigureAwait(false);

        RebuildCatalog();
        var packages = Evaluate(scan, records);
        Volatile.Write(ref _packages, packages);

        foreach (var package in packages)
            LogPackage(package);

        LogSummary("启动扫描", packages);
    }

    private void LogSummary(string trigger, IReadOnlyList<DriverPackageInfo> packages)
        => _logger.LogInformation(
            "驱动库 {Root}（{Trigger}）：驱动包 {Total} 个，已载入 {Loaded} 个，待确认 {Pending} 个，未编译的驱动源码 {NotBuilt} 个，第三方驱动 {Drivers} 个",
            RootDirectory, trigger, packages.Count(p => !p.IsSource), packages.Count(p => p.Status == DriverPackageStatus.Loaded),
            packages.Count(p => p.Status == DriverPackageStatus.PendingApproval), packages.Count(p => p.Status == DriverPackageStatus.NotBuilt),
            Drivers.Count);

    /// <summary>
    /// 启动时载入候选之间的驱动标识冲突：包标识 → 原因。与内置驱动重复只淘汰该包；两个包重复则两个都淘汰。
    /// </summary>
    private static Dictionary<string, string> StartupConflicts(IReadOnlyList<ScannedPackage> candidates)
    {
        var conflicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var builtIns = DriverCatalog.BuiltIns.Select(d => d.Id).ToHashSet();
        var owners = new Dictionary<DriverId, string>();

        foreach (var package in candidates)
        {
            var packageId = package.Manifest!.Id;
            foreach (var id in package.Manifest.Drivers.Select(d => d.Id))
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

    // ==================== 扫描与判定 ====================

    private async Task<Result<IReadOnlyList<DriverPackageInfo>>> ExclusiveAsync(
        Func<Task<Result<IReadOnlyList<DriverPackageInfo>>>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<DriverPackageInfo>>.Failure(DeviceLibraryErrors.ScanFailed("操作超时或已取消。"));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Error? Find(
        IReadOnlyList<DriverPackageInfo> packages,
        string packageId,
        string fingerprint,
        Func<DriverPackageInfo, bool> allowed)
    {
        // 源码项目可能与已移走但仍在使用的包同一标识：它不是驱动包，不参与信任 / 忽略
        var package = packages.FirstOrDefault(p => p.Id == packageId && !p.IsSource);
        if (package is null)
            return DeviceLibraryErrors.PackageNotFound(packageId);
        if (!string.Equals(package.Fingerprint, fingerprint, StringComparison.Ordinal))
            return DeviceLibraryErrors.FingerprintChanged(packageId);
        if (!allowed(package))
            return DeviceLibraryErrors.NotApprovable(packageId, package.Status);

        return null;
    }

    /// <summary>
    /// 不执行任何代码，只按磁盘现状、设置库记录与本次运行的载入情况给出每个包的状态
    /// </summary>
    private async Task<(IReadOnlyList<ScannedPackage> Scanned, IReadOnlyList<DriverPackageInfo> Packages)> RescanCoreAsync(
        CancellationToken cancellationToken)
    {
        var scan = ScanOrEmpty(RootDirectory, _logger, cancellationToken);
        var records = await ReadRecordsAsync(scan.Packages, cancellationToken).ConfigureAwait(false);
        var packages = Evaluate(scan, records);
        Volatile.Write(ref _packages, packages);
        return (scan.Packages, packages);
    }

    private IReadOnlyList<DriverPackageInfo> Evaluate(LibraryScan scan, Records records)
    {
        var scanned = scan.Packages;
        var duplicates = scanned
            .Where(p => p.Manifest is not null)
            .GroupBy(p => p.Manifest!.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        // 已占用的驱动标识：内置驱动 + 仍受信任的已载入包。别的包声明了同一标识就是冲突，信任前就能看出来
        var claims = DriverCatalog.BuiltIns.ToDictionary(d => d.Id, _ => "程序内置驱动");
        foreach (var packageId in _trusted)
        {
            foreach (var driver in _loaded[packageId].Drivers)
                claims.TryAdd(driver.Descriptor.Id, $"驱动包 {packageId}");
        }

        var results = new List<DriverPackageInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in scanned)
        {
            var info = Evaluate(package, records, duplicates, claims);
            results.Add(info);
            if (package.Manifest is { } m && !duplicates.Contains(m.Id))
                seen.Add(m.Id);
        }

        // 已载入的包目录被移走：本次运行仍在使用（若仍受信任），重启后不再载入
        foreach (var loaded in _loaded.Values.Where(p => !seen.Contains(p.Id)))
        {
            var inUse = _trusted.Contains(loaded.Id);
            results.Add(new DriverPackageInfo(loaded.Id, loaded.DirectoryName, loaded.Manifest.Name, loaded.Manifest.Version,
                loaded.Manifest.Author, loaded.Fingerprint, inUse ? DriverPackageStatus.Loaded : DriverPackageStatus.Revoked,
                inUse ? "包目录已移除：本次运行仍在使用，重启后不再载入。" : "包目录已移除。", Entries(loaded))
            {
                IsInUse = inUse,
            });
        }

        // 驱动源码：驱动库里已有同一包标识的驱动包，说明编译过，不再单独列出；否则提示"未编译"（清单无效的提示"无效"）
        var built = scanned.Where(p => p.Manifest is not null).Select(p => p.Manifest!.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var source in scan.Sources)
        {
            if (source.Manifest is { } m && built.Contains(m.Id))
                continue;

            results.Add(Evaluate(source));
        }

        // 需要操作员处理的排在前面：待确认 → 异常 → 其余，同组按包标识
        return results
            .OrderBy(p => p.Status switch
            {
                DriverPackageStatus.PendingApproval => 0,
                DriverPackageStatus.Conflict or DriverPackageStatus.Invalid or DriverPackageStatus.Incompatible
                    or DriverPackageStatus.LoadFailed or DriverPackageStatus.ApprovedRestartRequired
                    or DriverPackageStatus.NotBuilt => 1,
                _ => 2,
            })
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ThenBy(p => p.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private DriverPackageInfo Evaluate(
        ScannedPackage package,
        Records records,
        IReadOnlySet<string> duplicates,
        IReadOnlyDictionary<DriverId, string> claims)
    {
        if (package.Manifest is not { } manifest)
        {
            return new DriverPackageInfo(package.DirectoryName, package.DirectoryName, package.DirectoryName, "-", "-",
                package.Fingerprint, DriverPackageStatus.Invalid, string.Join("；", package.Problems), []);
        }

        var id = manifest.Id;
        records.Approvals.TryGetValue(id, out var approved);
        records.Ignored.TryGetValue(id, out var ignored);
        _loaded.TryGetValue(id, out var loaded);
        var inUse = loaded is not null && _trusted.Contains(id);

        // 正在使用的包显示已载入版本的驱动（可逐个启停）；否则显示磁盘上这份清单的声明
        var entries = loaded is not null ? Entries(loaded) : Entries(manifest);
        var info = new DriverPackageInfo(id, package.DirectoryName, manifest.Name, manifest.Version, manifest.Author,
            package.Fingerprint, DriverPackageStatus.PendingApproval, null, entries)
        {
            IsInUse = inUse,
            WasApproved = !string.IsNullOrEmpty(approved) && approved != package.Fingerprint,
        };

        if (duplicates.Contains(id))
            return info with { Status = DriverPackageStatus.Conflict, Problem = $"多个目录使用了同一个包标识 {id}。" };

        if (!SdkCompatible(manifest))
        {
            var unmapped = manifest.Drivers.FirstOrDefault(d => !CategoryMap.TryMap(d.Category, out _));
            return info with
            {
                Status = DriverPackageStatus.Incompatible,
                Problem = manifest.SdkVersion != SdkInfo.MajorVersion
                    ? $"驱动包需要 SDK {manifest.SdkVersion}，本程序提供 SDK {SdkInfo.MajorVersion}。"
                    : $"驱动 {unmapped?.Id} 的分类 {unmapped?.Category} 本程序尚不支持，请更新主程序。",
            };
        }

        if (loaded is not null)
        {
            if (loaded.Fingerprint == package.Fingerprint)
                return info with { Status = inUse ? DriverPackageStatus.Loaded : DriverPackageStatus.Revoked };

            // 磁盘上换成了另一个版本：本次运行的代码无法替换
            var note = inUse ? "本次运行仍使用已载入的版本。" : null;
            if (approved == package.Fingerprint)
                return info with { Status = DriverPackageStatus.ApprovedRestartRequired, Problem = "已信任新版本，重启后替换已载入的版本。" };

            return ConflictOr(info with
            {
                Status = ignored == package.Fingerprint ? DriverPackageStatus.Ignored : DriverPackageStatus.PendingApproval,
                Problem = $"包文件已改动。{note}".Trim(),
            }, manifest, claims);
        }

        if (_failures.TryGetValue(id, out var failure) && failure.Fingerprint == package.Fingerprint)
            return info with { Status = failure.Status, Problem = failure.Problem };

        if (approved == package.Fingerprint)
        {
            // 已信任但本次运行没有载入（例如在别处写入的信任记录）：不在扫描时执行代码，重启后载入
            return ConflictOr(info with { Status = DriverPackageStatus.ApprovedRestartRequired, Problem = "已信任，重启后载入。" },
                manifest, claims);
        }

        var status = ignored == package.Fingerprint ? DriverPackageStatus.Ignored : DriverPackageStatus.PendingApproval;
        return ConflictOr(info with { Status = status, Problem = info.WasApproved ? "内容与上次信任的版本不同。" : null }, manifest, claims);
    }

    /// <summary>
    /// 还没有编译出驱动包的驱动源码项目（或清单无效的源码项目）。只是提示，不能信任、不执行任何代码
    /// </summary>
    private static DriverPackageInfo Evaluate(SourceProject source)
    {
        if (source.Manifest is not { } manifest)
        {
            return new DriverPackageInfo(source.RelativeDirectory, source.RelativeDirectory, source.RelativeDirectory, "-", "-",
                string.Empty, DriverPackageStatus.Invalid,
                $"驱动源码项目的 {SdkInfo.ManifestFileName} 无效：{string.Join("；", source.Problems)}", [])
            {
                IsSource = true,
            };
        }

        return new DriverPackageInfo(manifest.Id, source.RelativeDirectory, manifest.Name, manifest.Version, manifest.Author,
            string.Empty, DriverPackageStatus.NotBuilt,
            $"这是驱动源码，驱动库中还没有它编译出的驱动包。编译 {source.ProjectFile}（或整个解决方案）后，" +
            $"驱动包会自动输出到驱动库下的 {manifest.Id} 目录，再点「扫描」即可信任载入。只运行主程序不会编译驱动项目。",
            Entries(manifest))
        {
            IsSource = true,
        };
    }

    private static DriverPackageInfo ConflictOr(DriverPackageInfo info, PackageManifest manifest, IReadOnlyDictionary<DriverId, string> claims)
    {
        foreach (var driver in manifest.Drivers)
        {
            if (claims.TryGetValue(driver.Id, out var owner) && owner != $"驱动包 {manifest.Id}")
                return info with { Status = DriverPackageStatus.Conflict, Problem = $"驱动标识 {driver.Id} 与{owner}重复。" };
        }

        return info;
    }

    private static bool SdkCompatible(PackageManifest manifest)
        => manifest.SdkVersion == SdkInfo.MajorVersion && manifest.Drivers.All(d => CategoryMap.TryMap(d.Category, out _));

    private static IReadOnlyList<DriverEntryInfo> Entries(PackageManifest manifest)
        => manifest.Drivers
            .Select(d => new DriverEntryInfo(d.Id, d.Name, CategoryMap.TryMap(d.Category, out var c) ? c : HardwareCategory.General,
                d.Manufacturer, d.Model))
            .ToList();

    private IReadOnlyList<DriverEntryInfo> Entries(LoadedPackage loaded)
        => loaded.Drivers
            .Select(d => new DriverEntryInfo(d.Descriptor.Id, d.Descriptor.Name, d.Descriptor.Category, d.Declared.Manufacturer, d.Declared.Model)
            {
                IsLoaded = true,
                IsEnabled = !_disabled.Contains(d.Descriptor.Id),
                MinAddress = d.Descriptor.MinAddress,
                MaxAddress = d.Descriptor.MaxAddress,
            })
            .ToList();

    private static LibraryScan ScanOrEmpty(string root, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(root);
            return PackageScanner.Scan(root, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogError(ex, "无法读取驱动库目录 {Root}，本次没有第三方驱动", root);
            return LibraryScan.Empty;
        }
    }

    private sealed record Records(IReadOnlyDictionary<string, string> Approvals, IReadOnlyDictionary<string, string> Ignored);

    /// <summary>
    /// 读取扫描到的各包的信任与忽略记录。读取失败按"未信任 / 未忽略"处理：宁可少载入一个驱动，也不载入未经信任的代码
    /// </summary>
    private async Task<Records> ReadRecordsAsync(IReadOnlyList<ScannedPackage> scanned, CancellationToken cancellationToken)
    {
        var approvals = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in scanned.Where(p => p.Manifest is not null).Select(p => p.Manifest!.Id).Distinct(StringComparer.Ordinal))
        {
            if (await ReadSettingAsync(ApprovalKeyPrefix + id, cancellationToken).ConfigureAwait(false) is { Length: > 0 } approval)
                approvals[id] = approval;
            if (await ReadSettingAsync(IgnoredKeyPrefix + id, cancellationToken).ConfigureAwait(false) is { Length: > 0 } ignore)
                ignored[id] = ignore;
        }

        return new Records(approvals, ignored);
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken)
    {
        var read = await _settings.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (read.IsSuccess)
            return read.Value;

        _logger.LogError("读取设置 {Key} 失败：{Error}", key, read.Error);
        return null;
    }

    // ==================== 载入与驱动目录 ====================

    private sealed record LoadedDriver(DriverDescriptor Descriptor, IDeviceDriver Implementation, ManifestDriver Declared);

    private sealed record LoadedPackage(
        string Id,
        string DirectoryName,
        string Fingerprint,
        PackageManifest Manifest,
        IReadOnlyList<LoadedDriver> Drivers);

    private sealed record Failure(string Fingerprint, DriverPackageStatus Status, string Problem);

    private readonly record struct LoadOutcome(LoadedPackage? Package, DriverPackageStatus Status, string? Problem)
    {
        public static LoadOutcome Failed(string problem) => new(null, DriverPackageStatus.LoadFailed, problem);
    }

    /// <summary>
    /// 载入一个已信任的包；成功则加入信任集合并读取其驱动的停用设置，失败则记下原因（同一指纹不再重试）
    /// </summary>
    private async Task LoadAndTrustAsync(ScannedPackage package, CancellationToken cancellationToken)
    {
        var manifest = package.Manifest!;
        var outcome = TryLoad(package, _logger);
        if (outcome.Package is not { } loaded)
        {
            _failures[manifest.Id] = new Failure(package.Fingerprint, outcome.Status, outcome.Problem ?? "载入失败。");
            _logger.LogError("驱动包 {PackageId} 载入失败：{Status} {Problem}", manifest.Id, outcome.Status, outcome.Problem);
            return;
        }

        foreach (var driver in loaded.Drivers)
        {
            // 读不到停用设置时按停用处理：操作员可能是有意隔离这台设备，不能因为读库失败就恢复轮询
            var value = await _settings.GetAsync(DisabledDriverKeyPrefix + driver.Descriptor.Id, cancellationToken).ConfigureAwait(false);
            if (value.IsFailure)
            {
                _logger.LogError("读取驱动 {DriverId} 的停用设置失败，按停用处理：{Error}", driver.Descriptor.Id, value.Error);
                _disabled.Add(driver.Descriptor.Id);
            }
            else if (value.Value == DisabledValue)
            {
                _disabled.Add(driver.Descriptor.Id);
            }
        }

        _loaded[manifest.Id] = loaded;
        _trusted.Add(manifest.Id);
        _failures.Remove(manifest.Id);
        _logger.LogInformation("驱动包 {PackageId} {Name} {Version}（作者 {Author}）已载入，驱动：{Drivers}",
            manifest.Id, manifest.Name, manifest.Version, manifest.Author, string.Join("、", loaded.Drivers.Select(d => d.Descriptor.Name)));
    }

    /// <summary>
    /// 按信任集合与停用设置重建驱动目录并整体替换。冲突在信任前已按清单声明排除，这里再防一道：重复的驱动跳过并记录。
    /// </summary>
    private void RebuildCatalog()
    {
        var descriptors = new List<DriverDescriptor>(DriverCatalog.BuiltIns);
        var ids = descriptors.Select(d => d.Id).ToHashSet();
        var implementations = new Dictionary<DriverId, IDeviceDriver>();

        foreach (var packageId in _trusted.Order(StringComparer.Ordinal))
        {
            foreach (var driver in _loaded[packageId].Drivers.Where(d => !_disabled.Contains(d.Descriptor.Id)))
            {
                if (!ids.Add(driver.Descriptor.Id))
                {
                    _logger.LogCritical("驱动标识 {DriverId}（驱动包 {PackageId}）与已有驱动重复，已跳过", driver.Descriptor.Id, packageId);
                    continue;
                }

                descriptors.Add(driver.Descriptor);
                implementations[driver.Descriptor.Id] = driver.Implementation;
            }
        }

        _catalog.Replace(new DriverCatalog(descriptors, implementations));
    }

    /// <summary>
    /// 在独立上下文中载入包并读取驱动说明，再与清单声明逐项核对。这里是执行第三方代码的边界：包内任何异常都只让本包载入失败。
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
                if (ToDescriptor(implementation?.Info, out var problem) is not { } descriptor)
                {
                    problems.Add(problem!);
                    continue;
                }

                if (drivers.Exists(d => d.Descriptor.Id == descriptor.Id))
                {
                    problems.Add($"包内驱动标识重复：{descriptor.Id}。");
                    continue;
                }

                var declared = manifest.Drivers.FirstOrDefault(d => d.Id == descriptor.Id);
                if (declared is null)
                {
                    problems.Add($"代码中的驱动 {descriptor.Id} 没有在清单中声明。");
                    continue;
                }

                // 清单是操作员信任时看到的内容：名称与分类必须与代码一致，防止"看到的是 A、运行的是 B"
                if (!string.Equals(declared.Name, descriptor.Name, StringComparison.Ordinal))
                    problems.Add($"驱动 {descriptor.Id} 的名称与清单不一致：清单为「{declared.Name}」，代码为「{descriptor.Name}」。");
                if (!CategoryMap.TryMap(declared.Category, out var declaredCategory) || declaredCategory != descriptor.Category)
                    problems.Add($"驱动 {descriptor.Id} 的分类与清单不一致。");
                if (CheckSimulation(implementation!, descriptor.Id) is { } simulation)
                    problems.Add(simulation);

                drivers.Add(new LoadedDriver(descriptor, implementation!, declared));
            }

            foreach (var missing in manifest.Drivers.Where(d => !drivers.Exists(l => l.Descriptor.Id == d.Id)))
            {
                if (!problems.Exists(p => p.Contains(missing.Id.ToString(), StringComparison.Ordinal)))
                    problems.Add($"清单声明的驱动 {missing.Id} 在代码中不存在。");
            }

            return problems.Count == 0
                ? new LoadOutcome(new LoadedPackage(manifest.Id, package.DirectoryName, package.Fingerprint, manifest, drivers),
                    DriverPackageStatus.Loaded, null)
                : LoadOutcome.Failed(string.Join("；", problems));
        }
        catch (Exception ex)
        {
            var root = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            logger.LogError(root, "驱动包 {PackageId} 载入失败", manifest.Id);
            return LoadOutcome.Failed($"{root.GetType().Name}：{root.Message}");
        }
    }

    /// <summary>
    /// SDK 1.4：每个驱动都必须带仿真设备。缺少时返回面向操作员的原因，否则为 null
    /// </summary>
    /// <remarks>
    /// 载入时就拒绝，而不是等到仿真模式下模块按超时报通信异常：问题在信任驱动包的那一刻暴露，
    /// 原因直接显示在驱动库里。只检查类型是否实现了接口，不调用 <see cref="IDeviceSimulatorProvider.CreateSimulator"/>，
    /// 载入期不为核对而多执行第三方代码。
    /// </remarks>
    internal static string? CheckSimulation(IDeviceDriver implementation, DriverId id)
    {
        ArgumentNullException.ThrowIfNull(implementation);

        return implementation is IDeviceSimulatorProvider
            ? null
            : $"驱动 {id} 没有实现仿真设备（{nameof(IDeviceSimulatorProvider)}）。SDK 1.4 起每个驱动都必须带仿真设备，见驱动开发规范 5.7 节。";
    }

    private static DriverDescriptor? ToDescriptor(DriverInfo? info, out string? problem)
    {
        problem = null;
        if (info is null)
        {
            problem = "驱动说明为空。";
            return null;
        }

        // 先按 SDK 的规则整体校验：标识格式、地址范围、点位 / 命令 / 参数的 Key 与参数声明
        var declared = info.Validate();
        if (declared.Count > 0)
        {
            problem = string.Join("；", declared);
            return null;
        }

        if (!DriverId.TryParse(info.Id, out var id))
        {
            problem = $"驱动标识「{info.Id}」格式无效。";
            return null;
        }

        if (!CategoryMap.TryMap(info.Category, out var category))
        {
            problem = $"驱动 {info.Id} 的分类未定义：{(int)info.Category}。";
            return null;
        }

        var channels = new List<ChannelGroup>(info.Channels.Count);
        foreach (var group in info.Channels)
        {
            if (!ChannelKindMap.TryMap(group.Function, out var kind))
            {
                problem = $"驱动 {info.Id} 的通道组 {group.Key} 功能未定义：{(int)group.Function}。";
                return null;
            }

            channels.Add(new ChannelGroup(group.Key, group.Name.Trim(), kind, group.Count));
        }

        var descriptor = new DriverDescriptor(id, info.Name?.Trim() ?? string.Empty, category,
            info.Description?.Trim() ?? string.Empty, info.MinAddress, info.MaxAddress)
        {
            Channels = channels,
        };
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

    private void LogPackage(DriverPackageInfo package)
    {
        if (package.Status == DriverPackageStatus.Loaded)
            return;

        if (package.Status == DriverPackageStatus.NotBuilt)
        {
            _logger.LogWarning("驱动源码 {Directory}（包标识 {PackageId}）还没有编译出驱动包", package.Directory, package.Id);
            return;
        }

        _logger.LogWarning("驱动包 {PackageId} {Version}（目录 {Directory}，指纹 {Fingerprint}）未载入：{Status}，{Reason}",
            package.Id, package.Version, package.Directory, package.Fingerprint, package.Status,
            package.Problem ?? (package.Status == DriverPackageStatus.PendingApproval ? "等待操作员在驱动库中信任" : "-"));
    }
}
