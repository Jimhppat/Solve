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
/// 用示例驱动包（Sample.DriverPackage，随测试一起编译）在临时目录里搭设备库，验证发现、确认、加载与各种异常包。
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"), "DeviceLibrary");
    private readonly MemorySettings _settings = new();

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent))
            Directory.Delete(parent, recursive: true);
    }

    private Task<DeviceLibraryManager> LoadAsync()
        => DeviceLibraryManager.LoadAsync(_root, _settings, NullLogger.Instance, CancellationToken.None);

    /// <summary>
    /// 在设备库里放一个示例驱动包；可覆盖清单字段
    /// </summary>
    private string AddPackage(string directory, string id = "sample.drivers", int sdkVersion = SdkInfo.MajorVersion,
        string entryAssembly = SampleAssembly, string entryType = SampleEntryType, bool copyAssembly = true)
    {
        var path = Path.Combine(_root, directory);
        Directory.CreateDirectory(path);
        if (copyAssembly)
            File.Copy(Path.Combine(AppContext.BaseDirectory, SampleAssembly), Path.Combine(path, SampleAssembly));

        var manifest = new
        {
            id,
            name = "示例驱动包",
            version = "1.0.0",
            author = "Solve",
            sdkVersion,
            entryAssembly,
            entryType,
        };
        File.WriteAllText(Path.Combine(path, SdkInfo.ManifestFileName), JsonSerializer.Serialize(manifest));
        return path;
    }

    private async Task ApproveCurrentAsync(string packageId = "sample.drivers")
    {
        var pending = (await LoadAsync()).Packages.Single(p => p.Id == packageId);
        _settings.Values[DeviceLibraryManager.ApprovalKeyPrefix + packageId] = pending.Fingerprint;
    }

    // ---------- 发现与确认 ----------

    [Fact]
    public async Task MissingDirectory_IsCreated_AndOnlyBuiltInsExist()
    {
        var library = await LoadAsync();

        Assert.True(Directory.Exists(_root));
        Assert.Empty(library.Packages);
        Assert.Equal([DriverCatalog.GenericModule], library.Catalog.All.Select(d => d.Id));
    }

    [Fact]
    public async Task NewPackage_IsPendingApproval_AndNotLoaded()
    {
        AddPackage("sample");

        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.PendingApproval, package.Status);
        Assert.True(package.CanApprove);
        Assert.False(library.Catalog.TryGet(SamplePower, out _));
        Assert.Empty(library.Drivers);
    }

    [Fact]
    public async Task ApprovedPackage_IsLoaded_IntoCatalog()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();

        var library = await LoadAsync();

        var package = Assert.Single(library.Packages);
        Assert.Equal(DriverPackageStatus.Loaded, package.Status);
        Assert.Equal(["示例电源"], package.DriverNames);
        Assert.True(library.Catalog.TryGet(SamplePower, out var driver));
        Assert.Equal((HardwareCategory.PowerAndLoad, 1, 32), (driver.Category, driver.MinAddress, driver.MaxAddress));
        Assert.True(library.Catalog.TryGet(DriverCatalog.GenericModule, out _));
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
        Assert.Equal(TimeSpan.Zero, send.DelayAfter);

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
    public async Task LoadedDriver_UsesParameters_AndClassifiesFailures()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();
        var driver = library.Drivers[SamplePower];

        // 驱动声明的参数通过了 SDK 校验；命令按参数给出稳定时间
        Assert.Empty(driver.Info.Validate());
        var session = driver.CreateSession(SampleContext(address: 1, settleMs: "500"));
        var on = Assert.IsType<ExchangeStep.Send>(session.BeginCommand(new DeviceCommand("output.on", new Dictionary<string, double>())).Start());
        Assert.Equal(TimeSpan.FromMilliseconds(500), on.DelayAfter);

        // 不支持的命令不应重试：配置错误
        var unknown = session.BeginCommand(new DeviceCommand("self-destruct", new Dictionary<string, double>())).Start();
        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeStep.Failed>(unknown).Kind);
    }

    [Fact]
    public async Task ChangedFile_AfterApproval_NeedsApprovalAgain()
    {
        var path = AddPackage("sample");
        await ApproveCurrentAsync();

        File.WriteAllText(Path.Combine(path, "readme.txt"), "多了一个文件");
        var library = await LoadAsync();

        Assert.Equal(DriverPackageStatus.PendingApproval, Assert.Single(library.Packages).Status);
        Assert.False(library.Catalog.TryGet(SamplePower, out _));
    }

    [Fact]
    public async Task Approve_TakesEffectAfterRestart()
    {
        AddPackage("sample");
        var library = await LoadAsync();
        var pending = Assert.Single(library.Packages);

        var approved = await library.ApproveAsync(pending.Id, pending.Fingerprint, CancellationToken.None);

        Assert.Equal(DriverPackageStatus.ApprovedRestartRequired, Assert.Single(approved.Value).Status);
        Assert.False(library.Catalog.TryGet(SamplePower, out _));

        var restarted = await LoadAsync();
        Assert.Equal(DriverPackageStatus.Loaded, Assert.Single(restarted.Packages).Status);
    }

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
    }

    [Fact]
    public async Task Revoke_KeepsCurrentRun_AndStopsLoadingAfterRestart()
    {
        AddPackage("sample");
        await ApproveCurrentAsync();
        var library = await LoadAsync();

        var revoked = await library.RevokeAsync("sample.drivers", CancellationToken.None);

        Assert.Equal(DriverPackageStatus.RevokedRestartRequired, Assert.Single(revoked.Value).Status);
        Assert.True(library.Catalog.TryGet(SamplePower, out _));

        var restarted = await LoadAsync();
        Assert.Equal(DriverPackageStatus.PendingApproval, Assert.Single(restarted.Packages).Status);
    }

    [Fact]
    public async Task Rescan_FindsNewPackages_WithoutLoadingThem()
    {
        var library = await LoadAsync();
        AddPackage("sample");

        var rescanned = await library.RescanAsync(CancellationToken.None);

        Assert.Equal(DriverPackageStatus.PendingApproval, Assert.Single(rescanned.Value).Status);
        Assert.Same(rescanned.Value, library.Packages);
    }

    // ---------- 异常包 ----------

    [Fact]
    public async Task MissingManifest_IsInvalid()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

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
    public async Task SameDriverIdInTwoPackages_BothConflict_AndNeitherIsLoaded()
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
