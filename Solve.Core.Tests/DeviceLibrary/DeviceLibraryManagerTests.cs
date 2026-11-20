using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.DeviceLibrary;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.DeviceLibrary;

/// <summary>
/// 用示例驱动包（Sample.DriverPackage，随测试一起编译）在临时目录里搭设备库，
/// 验证扫描（不执行代码就能列出驱动）、信任即载入、撤销、忽略、驱动启停、导入与各种异常包。
/// </summary>
public sealed class DeviceLibraryManagerTests : IDisposable
{
    private sealed class MemorySettings : IAppSettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<Result<string?>> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(Result<string?>.Success(Values.GetValueOrDefault(key)));

        public Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.FromResult(Result.Success());
        }
    }

    private const string SampleAssembly = "Sample.DriverPackage.dll";
    private const string SampleEntryType = "Sample.DriverPackage.SampleDriverPackage";
    private static readonly DriverId SamplePower = DriverId.Of("sample.power");

    private static readonly object[] SampleDrivers =
    [
        new { id = "sample.power", name = "示例电源", category = "PowerAndLoad", manufacturer = "Solve", model = "示例" },
    ];

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();

    private string Root => Path.Combine(_workspace, "DeviceLibrary");

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private Task<DeviceLibraryManager> LoadAsync()
        => DeviceLibraryManager.LoadAsync(Root, _settings, NullLogger.Instance, CancellationToken.None);

    /// <summary>
    /// 在设备库（或任意目录）里放一个示例驱动包；可覆盖清单字段
    /// </summary>
    private string AddPackage(string directory, string id = "sample.drivers", int sdkVersion = SdkInfo.MajorVersion,
        string entryAssembly = SampleAssembly, string entryType = SampleEntryType, bool copyAssembly = true,
        object[]? drivers = null, string? parent = null, string version = "1.0.0")
    {
        var path = Path.Combine(parent ?? Root, directory);
        Directory.CreateDirectory(path);
        if (copyAssembly)
            File.Copy(Path.Combine(AppContext.BaseDirectory, SampleAssembly), Path.Combine(path, SampleAssembly), overwrite: true);

        var manifest = new
        {
            id,
            name = "示例驱动包",
            version,
            author = "Solve",
            sdkVersion,
            entryAssembly,
            entryType,
            drivers = drivers ?? SampleDrivers,
        };
        File.WriteAllText(Path.Combine(path, SdkInfo.ManifestFileName), JsonSerializer.Serialize(manifest));
        return path;
    }

    private async Task ApproveCurrentAsync(string packageId = "sample.drivers")
    {
        var pending = (await LoadAsync()).Packages.Single(p => p.Id == packageId);
        _settings.Values[DeviceLibraryManager.ApprovalKeyPrefix + packageId] = pending.Fingerprint;
    }

    private static async Task<DriverPackageInfo> ApproveAsync(DeviceLibraryManager library, string packageId = "sample.drivers")
    {
        var pending = library.Packages.Single(p => p.Id == packageId);
        var approved = await library.ApproveAsync(pending.Id, pending.Fingerprint, CancellationToken.None);
        Assert.True(approved.IsSuccess, approved.ToString());
        return approved.Value.Single(p => p.Id == packageId);
    }

    // ---------- 扫描：不执行代码就能看到包内驱动 ----------

    [Fact]
    public async Task MissingDirectory_IsCreated_AndOnlyBuiltInsExist()
    {
        var library = await LoadAsync();

        Assert.True(Directory.Exists(Root));
        Assert.Empty(library.Packages);
        Assert.Equal([DriverCatalog.GenericModule], library.Catalog.All.Select(d => d.Id));
    }

    [Fact]
    public async Task NewPackage_IsPending_AndListsDeclaredDrivers_WithoutLoading()
    {
        AddPackage("sample");

        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.PendingApproval, package.Status);
        Assert.True(package.CanApprove);
        Assert.False(package.IsInUse);
        var driver = Assert.Single(package.Drivers);
        Assert.Equal((SamplePower, "示例电源", HardwareCategory.PowerAndLoad, "Solve", "示例", false),
            (driver.Id, driver.Name, driver.Category, driver.Manufacturer, driver.Model, driver.IsLoaded));
        Assert.False(library.Catalog.TryGet(SamplePower, out _));
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task Rescan_FindsNewPackages_WithoutLoadingThem()
    {
        var library = await LoadAsync();
        AddPackage("sample");

        var rescanned = await library.RescanAsync(CancellationToken.None);

        Assert.Equal(DriverPackageStatus.PendingApproval, Assert.Single(rescanned.Value).Status);
        Assert.Same(rescanned.Value, library.Packages);
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task PendingPackages_AreListedFirst()
    {
        AddPackage("a", id: "a.loaded");
        await ApproveCurrentAsync("a.loaded");
        AddPackage("z", id: "z.pending", drivers: [new { id = "z.power", name = "示例电源", category = "PowerAndLoad" }]);

        var packages = (await LoadAsync()).Packages;

        Assert.Equal(["z.pending", "a.loaded"], packages.Select(p => p.Id));
        Assert.Equal([DriverPackageStatus.PendingApproval, DriverPackageStatus.Loaded], packages.Select(p => p.Status));
    }

    // ---------- 信任即载入 ----------

    [Fact]
    public async Task ApprovedAtStartup_IsLoaded_IntoCatalog()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();

        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.Loaded, package.Status);
        Assert.True(package.IsInUse);
        var entry = Assert.Single(package.Drivers);
        Assert.Equal((true, true, 1, 32), (entry.IsLoaded, entry.IsEnabled, entry.MinAddress, entry.MaxAddress));
        Assert.True(library.Catalog.TryGet(SamplePower, out var driver));
        Assert.Equal((HardwareCategory.PowerAndLoad, 1, 32), (driver.Category, driver.MinAddress, driver.MaxAddress));
        Assert.True(library.Catalog.TryGet(DriverCatalog.GenericModule, out _));
    }

    [Fact]
    public async Task Approve_LoadsImmediately_AndRaisesCatalogChanged()
    {
        AddPackage("sample");
        var library = await LoadAsync();
        var changed = 0;
        library.Catalog.Changed += (_, _) => changed++;

        var approved = await ApproveAsync(library);

        Assert.Equal(DriverPackageStatus.Loaded, approved.Status);
        Assert.True(library.Catalog.TryGetImplementation(SamplePower, out _));
        Assert.Equal(1, changed);

        // 重启后照样载入
        Assert.Equal(DriverPackageStatus.Loaded, Assert.Single((await LoadAsync()).Packages).Status);
    }

    [Fact]
    public async Task LoadedDriver_UsesSharedSdkTypes_AndEncodesRequests()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();

        // 驱动来自独立加载上下文，但与内核共享 SDK 类型：能直接按 SDK 接口调用
        var session = library.Drivers[SamplePower].CreateSession(SampleContext(address: 3, settleMs: "200"));
        var exchange = session.BeginPoll();
        var send = Assert.IsType<ExchangeStep.Send>(exchange.Start());

        Assert.Equal(new byte[] { 0xAA, 3, 0x01, 0xAE }, send.Request.ToArray());
        Assert.Equal(new FrameGap(3.5, TimeSpan.FromMilliseconds(2)), send.GapBefore);

        // 响应：电压 12.34 V（1234），电流 0.5 A（500）
        byte[] response = [0x55, 3, 0x01, 0x04, 0xD2, 0x01, 0xF4, 0];
        response[^1] = (byte)response[..^1].Sum(b => b);
        Assert.Equal(FrameStatus.NeedMoreData, exchange.Match(response.AsSpan(0, 5), out _));
        Assert.Equal(FrameStatus.Complete, exchange.Match(response, out var length));

        var done = Assert.IsType<ExchangeStep.Completed>(exchange.OnFrame(response.AsSpan(0, length)));
        Assert.Equal([new PointSample("voltage", 12.34), new PointSample("current", 0.5)], done.Samples);
    }

    private static DeviceContext SampleContext(int address, string settleMs)
        => new(address, LinkKind.Serial, new DeviceParameters(new Dictionary<string, string> { ["settle-ms"] = settleMs }));

    [Fact]
    public async Task Approve_WithStaleFingerprint_IsRefused()
    {
        var path = AddPackage("sample");
        var library = await LoadAsync();
        var pending = Assert.Single(library.Packages);

        File.WriteAllText(Path.Combine(path, "readme.txt"), "确认前被替换");
        var result = await library.ApproveAsync(pending.Id, pending.Fingerprint, CancellationToken.None);

        Assert.Equal(DeviceLibraryErrors.FingerprintChangedCode, result.Error.Code);
        Assert.Empty(_settings.Values);
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task ChangedFile_AfterApproval_NeedsApprovalAgain()
    {
        var path = AddPackage("sample");
        await ApproveCurrentAsync();

        File.WriteAllText(Path.Combine(path, "readme.txt"), "多了一个文件");
        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.PendingApproval, package.Status);
        Assert.True(package.WasApproved);
        Assert.False(library.Catalog.TryGet(SamplePower, out _));
    }

    [Fact]
    public async Task ChangedFile_WhileLoaded_KeepsRunningVersion_UntilRestart()
    {
        var path = AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();
        library.Drivers.TryGetValue(SamplePower, out var running);

        File.WriteAllText(Path.Combine(path, "readme.txt"), "新版本");
        var rescanned = Assert.Single((await library.RescanAsync(CancellationToken.None)).Value);
        Assert.Equal(DriverPackageStatus.PendingApproval, rescanned.Status);
        Assert.True(rescanned.IsInUse);

        var approved = await ApproveAsync(library);

        // 已载入的代码无法替换：新版本重启后生效，本次运行仍是原来的实现
        Assert.Equal(DriverPackageStatus.ApprovedRestartRequired, approved.Status);
        Assert.Same(running, library.Drivers[SamplePower]);
        Assert.Equal(DriverPackageStatus.Loaded, Assert.Single((await LoadAsync()).Packages).Status);
    }

    // ---------- 撤销、忽略 ----------

    [Fact]
    public async Task Revoke_RemovesDriversImmediately_AndReapproveRestoresSameCode()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();
        var running = library.Drivers[SamplePower];

        var revoked = Assert.Single((await library.RevokeAsync("sample.drivers", CancellationToken.None)).Value);

        Assert.Equal(DriverPackageStatus.Revoked, revoked.Status);
        Assert.True(revoked.CanApprove);
        Assert.False(library.Catalog.TryGet(SamplePower, out _));

        // 同一版本再次信任：恢复原实现，不重复载入
        var reapproved = await ApproveAsync(library);
        Assert.Equal(DriverPackageStatus.Loaded, reapproved.Status);
        Assert.Same(running, library.Drivers[SamplePower]);
    }

    [Fact]
    public async Task Revoke_StopsLoadingAfterRestart()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();
        await library.RevokeAsync("sample.drivers", CancellationToken.None);

        var restarted = await LoadAsync();

        Assert.Equal(DriverPackageStatus.PendingApproval, Assert.Single(restarted.Packages).Status);
        Assert.Empty(restarted.Drivers);
    }

    [Fact]
    public async Task Ignore_HidesThisVersion_UntilContentChanges()
    {
        var path = AddPackage("sample");
        var library = await LoadAsync();
        var pending = Assert.Single(library.Packages);

        var ignored = Assert.Single((await library.IgnoreAsync(pending.Id, pending.Fingerprint, CancellationToken.None)).Value);
        Assert.Equal(DriverPackageStatus.Ignored, ignored.Status);
        Assert.True(ignored.CanApprove);
        Assert.Equal(DriverPackageStatus.Ignored, Assert.Single((await LoadAsync()).Packages).Status);

        File.WriteAllText(Path.Combine(path, "readme.txt"), "新内容");
        Assert.Equal(DriverPackageStatus.PendingApproval, Assert.Single((await library.RescanAsync(CancellationToken.None)).Value).Status);
    }

    [Fact]
    public async Task IgnoredPackage_CanStillBeApproved()
    {
        AddPackage("sample");
        var library = await LoadAsync();
        var pending = Assert.Single(library.Packages);
        await library.IgnoreAsync(pending.Id, pending.Fingerprint, CancellationToken.None);

        Assert.Equal(DriverPackageStatus.Loaded, (await ApproveAsync(library)).Status);
    }

    // ---------- 驱动启停 ----------

    [Fact]
    public async Task DisableDriver_RemovesItFromCatalog_AndPersists()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();

        var disabled = Assert.Single((await library.SetDriverEnabledAsync(SamplePower, false, CancellationToken.None)).Value);

        Assert.False(Assert.Single(disabled.Drivers).IsEnabled);
        Assert.Equal(DriverPackageStatus.Loaded, disabled.Status);
        Assert.False(library.Catalog.TryGet(SamplePower, out _));

        var restarted = await LoadAsync();
        Assert.False(restarted.Catalog.TryGet(SamplePower, out _));

        await restarted.SetDriverEnabledAsync(SamplePower, true, CancellationToken.None);
        Assert.True(restarted.Catalog.TryGetImplementation(SamplePower, out _));
    }

    [Fact]
    public async Task DisableDriver_NotLoaded_IsRefused()
    {
        AddPackage("sample");
        var library = await LoadAsync();

        var result = await library.SetDriverEnabledAsync(SamplePower, false, CancellationToken.None);

        Assert.Equal(DeviceLibraryErrors.DriverNotFoundCode, result.Error.Code);
    }

    // ---------- 导入 ----------

    [Fact]
    public async Task Import_CopiesPackageIntoLibrary_AsPending()
    {
        var source = AddPackage("build-output", parent: Path.Combine(_workspace, "src"));
        var library = await LoadAsync();

        var imported = await library.ImportAsync(source, overwrite: false, CancellationToken.None);

        var package = Assert.Single(imported.Value);
        Assert.Equal(("sample.drivers", "sample.drivers", DriverPackageStatus.PendingApproval), (package.Id, package.Directory, package.Status));
        Assert.True(File.Exists(Path.Combine(Root, "sample.drivers", SampleAssembly)));
        Assert.DoesNotContain(Directory.GetDirectories(Root), d => Path.GetFileName(d).StartsWith('.'));
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task Import_SameContentAgain_IsNoOp_AndDifferentContentNeedsOverwrite()
    {
        var source = AddPackage("build-output", parent: Path.Combine(_workspace, "src"));
        var library = await LoadAsync();
        var first = Assert.Single((await library.ImportAsync(source, overwrite: false, CancellationToken.None)).Value);

        Assert.Equal(first.Fingerprint, Assert.Single((await library.ImportAsync(source, overwrite: false, CancellationToken.None)).Value).Fingerprint);

        AddPackage("build-output", parent: Path.Combine(_workspace, "src"), version: "1.1.0");
        var refused = await library.ImportAsync(source, overwrite: false, CancellationToken.None);
        Assert.Equal(DeviceLibraryErrors.AlreadyExistsCode, refused.Error.Code);

        var replaced = Assert.Single((await library.ImportAsync(source, overwrite: true, CancellationToken.None)).Value);
        Assert.Equal("1.1.0", replaced.Version);
        Assert.NotEqual(first.Fingerprint, replaced.Fingerprint);
        Assert.DoesNotContain(Directory.GetDirectories(Root), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public async Task Import_InvalidOrInsideLibrary_IsRefused()
    {
        var library = await LoadAsync();
        var empty = Directory.CreateDirectory(Path.Combine(_workspace, "empty")).FullName;
        var inside = AddPackage("sample");

        Assert.Equal(DeviceLibraryErrors.ImportFailedCode, (await library.ImportAsync(empty, false, CancellationToken.None)).Error.Code);
        Assert.Equal(DeviceLibraryErrors.ImportFailedCode, (await library.ImportAsync(inside, false, CancellationToken.None)).Error.Code);
        Assert.Equal(DeviceLibraryErrors.ImportFailedCode, (await library.ImportAsync(_workspace, false, CancellationToken.None)).Error.Code);
        Assert.Equal(DeviceLibraryErrors.ImportFailedCode,
            (await library.ImportAsync(Path.Combine(_workspace, "missing"), false, CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task Import_FromBuildOutputInsideLibrarySourceTree_IsAllowed()
    {
        // 驱动库与源码同根：Devicelibrary/Chroma/Chroma.Drivers/bin/... 是编译输出，可以导入
        var project = Directory.CreateDirectory(Path.Combine(Root, "Chroma", "Chroma.Drivers")).FullName;
        File.WriteAllText(Path.Combine(project, "Chroma.Drivers.csproj"), "<Project />");
        var output = AddPackage("net10.0", parent: Path.Combine(project, "bin", "Debug"));
        var library = await LoadAsync();

        var imported = await library.ImportAsync(output, overwrite: false, CancellationToken.None);

        Assert.True(imported.IsSuccess, imported.ToString());
        Assert.Equal("sample.drivers", Assert.Single(imported.Value).Directory);
    }

    [Fact]
    public async Task SourceDirectories_AreSkipped_NotReportedInvalid()
    {
        // 驱动源码与驱动包放在同一个根目录：厂家目录 / 项目目录 / *.csproj，根部没有清单
        var project = Directory.CreateDirectory(Path.Combine(Root, "Chroma", "Chroma.Drivers")).FullName;
        File.WriteAllText(Path.Combine(project, "Chroma.Drivers.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(project, "package.json"), "{}");
        AddPackage("sample.drivers");

        var package = Assert.Single((await LoadAsync()).Packages);

        Assert.Equal("sample.drivers", package.Directory);
        Assert.Equal(DriverPackageStatus.PendingApproval, package.Status);
    }

    [Fact]
    public async Task HiddenFiles_AreNotPartOfPackage_AndDoNotChangeFingerprint()
    {
        var path = AddPackage("sample");
        var before = Assert.Single((await LoadAsync()).Packages).Fingerprint;

        // 编译后自动产出的包里会写一个 .gitignore；隐藏目录同理
        File.WriteAllText(Path.Combine(path, ".gitignore"), "*");
        Directory.CreateDirectory(Path.Combine(path, ".cache"));
        File.WriteAllText(Path.Combine(path, ".cache", "x.dll"), "不是程序集");

        Assert.Equal(before, Assert.Single((await LoadAsync()).Packages).Fingerprint);
    }

    [Fact]
    public async Task StagingDirectories_AreNotPackages()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".import-leftover"));

        Assert.Empty((await LoadAsync()).Packages);
    }

    // ---------- 清单声明与代码核对 ----------

    [Fact]
    public async Task ManifestWithoutDrivers_IsInvalid()
    {
        AddPackage("sample", drivers: []);

        var package = Assert.Single((await LoadAsync()).Packages);

        Assert.Equal(DriverPackageStatus.Invalid, package.Status);
        Assert.Contains("drivers", package.Problem);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("2")]
    public async Task ManifestWithUndefinedCategory_IsInvalid(string category)
    {
        AddPackage("sample", drivers: [new { id = "sample.power", name = "示例电源", category }]);

        Assert.Equal(DriverPackageStatus.Invalid, Assert.Single((await LoadAsync()).Packages).Status);
    }

    [Fact]
    public async Task DeclaredNameDiffersFromCode_FailsToLoad()
    {
        AddPackage("sample", drivers: [new { id = "sample.power", name = "看起来很安全的驱动", category = "PowerAndLoad" }]);
        await ApproveCurrentAsync();

        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.LoadFailed, package.Status);
        Assert.Contains("名称与清单不一致", package.Problem);
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task UndeclaredDriverInCode_FailsToLoad()
    {
        AddPackage("sample", drivers: [new { id = "sample.other", name = "别的驱动", category = "General" }]);
        await ApproveCurrentAsync();

        var package = Assert.Single((await LoadAsync()).Packages);

        Assert.Equal(DriverPackageStatus.LoadFailed, package.Status);
        Assert.Contains("没有在清单中声明", package.Problem);
    }

    // ---------- 异常包 ----------

    [Fact]
    public async Task MissingManifest_IsInvalid()
    {
        Directory.CreateDirectory(Path.Combine(Root, "empty"));

        var package = Assert.Single((await LoadAsync()).Packages);

        Assert.Equal(DriverPackageStatus.Invalid, package.Status);
        Assert.Contains(SdkInfo.ManifestFileName, package.Problem);
    }

    [Fact]
    public async Task MissingEntryAssembly_IsInvalid()
    {
        AddPackage("sample", copyAssembly: false);

        Assert.Equal(DriverPackageStatus.Invalid, Assert.Single((await LoadAsync()).Packages).Status);
    }

    [Fact]
    public async Task OtherSdkVersion_IsIncompatible()
    {
        AddPackage("sample", sdkVersion: SdkInfo.MajorVersion + 1);

        Assert.Equal(DriverPackageStatus.Incompatible, Assert.Single((await LoadAsync()).Packages).Status);
    }

    [Fact]
    public async Task SamePackageIdInTwoDirectories_BothConflict()
    {
        AddPackage("a");
        AddPackage("b");

        var packages = (await LoadAsync()).Packages;

        Assert.Equal(2, packages.Count);
        Assert.All(packages, p => Assert.Equal(DriverPackageStatus.Conflict, p.Status));
    }

    [Fact]
    public async Task SameDriverIdInTwoApprovedPackages_BothConflict_AndNeitherIsLoaded()
    {
        AddPackage("a", id: "vendor.a");
        AddPackage("b", id: "vendor.b");
        await ApproveCurrentAsync("vendor.a");
        await ApproveCurrentAsync("vendor.b");

        var library = await LoadAsync();

        Assert.All(library.Packages, p => Assert.Equal(DriverPackageStatus.Conflict, p.Status));
        Assert.False(library.Catalog.TryGet(SamplePower, out _));
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task PendingPackage_ClaimingLoadedDriverId_IsConflict_BeforeAnyCodeRuns()
    {
        AddPackage("a", id: "vendor.a");
        await ApproveCurrentAsync("vendor.a");
        AddPackage("b", id: "vendor.b");

        var library = await LoadAsync();

        var other = library.Packages.Single(p => p.Id == "vendor.b");
        Assert.Equal(DriverPackageStatus.Conflict, other.Status);
        Assert.False(other.CanApprove);
        Assert.Contains("vendor.a", other.Problem);
    }

    [Fact]
    public async Task WrongEntryType_FailsToLoad_WithoutAffectingStartup()
    {
        AddPackage("sample", entryType: "Sample.DriverPackage.DoesNotExist");
        await ApproveCurrentAsync();

        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.LoadFailed, package.Status);
        Assert.Contains("DoesNotExist", package.Problem);
        Assert.True(library.Catalog.TryGet(DriverCatalog.GenericModule, out _));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("sub/Sample.DriverPackage.dll")]
    [InlineData("Sample.DriverPackage.exe")]
    public async Task EntryAssemblyWithPath_IsInvalid(string entryAssembly)
    {
        AddPackage("sample", entryAssembly: entryAssembly);

        Assert.Equal(DriverPackageStatus.Invalid, Assert.Single((await LoadAsync()).Packages).Status);
    }

    [Fact]
    public async Task BadPackageId_IsInvalid()
    {
        AddPackage("sample", id: "Has Space");

        Assert.Equal(DriverPackageStatus.Invalid, Assert.Single((await LoadAsync()).Packages).Status);
    }
}
