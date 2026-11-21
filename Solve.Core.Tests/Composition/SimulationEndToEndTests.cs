using System.Text.Json;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Domain.Devices;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Composition;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.Composition;

/// <summary>
/// 端到端：真实组合根在仿真模式下，与操作员在界面上的步骤相同（信任驱动包 → 建档 → 建总线 → 挂模块 → 接线 → 连接总线），
/// 模块由驱动的仿真设备应答而上线，接线面板的判据（联锁）变为就绪。不触碰任何真实设备。
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class SimulationEndToEndTests : IAsyncLifetime
{
    private const string SampleAssembly = "Sample.DriverPackage.dll";
    private static readonly DriverId SamplePower = DriverId.Of("sample.power");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private AppHost? _host;

    private string DatabasePath => Path.Combine(_directory, "config", "solve.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();

        SqliteDatabase.ClearPool(new SqliteStorageOptions(DatabasePath));
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// 驱动库里放一个示例驱动包（随测试编译的 Sample.DriverPackage）
    /// </summary>
    private string CreateLibrary()
    {
        var library = Path.Combine(_directory, "DeviceLibrary");
        var package = Path.Combine(library, "sample");
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(AppContext.BaseDirectory, SampleAssembly), Path.Combine(package, SampleAssembly));
        File.WriteAllText(Path.Combine(package, SdkInfo.ManifestFileName), JsonSerializer.Serialize(new
        {
            id = "sample.drivers",
            name = "示例驱动包",
            version = "1.0.0",
            author = "Solve",
            sdkVersion = SdkInfo.MajorVersion,
            entryAssembly = SampleAssembly,
            entryType = "Sample.DriverPackage.SampleDriverPackage",
            drivers = new[] { new { id = "sample.power", name = "示例电源", category = "PowerAndLoad", manufacturer = "Solve", model = "示例" } },
        }));
        return library;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 50)
            await Task.Delay(50);

        Assert.True(condition(), "等待超时");
    }

    [Fact]
    public async Task Simulation_PowerModuleGoesOnline_AndItsWireBecomesReady()
    {
        _host = AppHost.Create(new DesktopOptions(false, DatabasePath, Path.Combine(_directory, "logs"),
            DeviceLibraryDirectory: CreateLibrary()));
        var report = await _host.Start();
        Assert.True(report.Succeeded, report.Fatal?.ToString());
        Assert.Equal(TransportMode.Simulation, report.Mode!.Mode);

        // 操作员在驱动库中信任示例驱动包
        var pending = _host.DeviceLibrary!.Packages.Single(p => p.Id == "sample.drivers");
        Assert.True((await _host.DeviceLibrary.ApproveAsync(pending.Id, pending.Fingerprint, CancellationToken.None)).IsSuccess);

        // 建档：1 柜 × 1 层 × 1 槽 × 1 治具 × 1 产品
        var topology = _host.Topology!;
        Assert.True((await topology.SaveAsync(topology.PlanLayout("老化房", new TopologyLayout(1, 1, 1, 1, 1)).Value, CancellationToken.None)).IsSuccess);
        var layer = topology.Current!.GetNode(TopologyAddress.Parse("C01-L01")).Value.Id;

        // 建总线、挂一台示例电源
        var bus = (await _host.BusService!.CreateBusAsync(new SerialBusDescriptor(BusId.New(), "1# RS-485", "COM7"), CancellationToken.None)).Value;
        var added = await _host.Devices!.AddBatchAsync(new DeviceBatchRequest(bus.Id, SamplePower, "电源", 1, 1), CancellationToken.None);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        var power = added.Value[0];

        // 接线：电源输出 1 接到层位
        var inspector = _host.Wiring!;
        await WaitUntilAsync(() => inspector.Wiring.PlanAdd(layer, new ChannelRef(power.Id, "output", 1), null, null).IsSuccess);
        var plan = inspector.Wiring.PlanAdd(layer, new ChannelRef(power.Id, "output", 1), null, null).Value;
        Assert.True((await inspector.Wiring.ApplyAsync(plan, CancellationToken.None)).IsSuccess);

        // 总线未连接：等待中，联锁拒绝
        Assert.NotEqual(WireHealth.Ready, inspector.Summarize(layer));
        Assert.True(inspector.CheckReady(layer).IsFailure);

        // 操作员连接总线（仿真通道）：仿真设备应答轮询，模块上线，接线就绪
        Assert.True(bus.Connect().IsSuccess);
        await WaitUntilAsync(() => inspector.CheckReady(layer).IsSuccess);
        Assert.Equal(WireHealth.Ready, inspector.Summarize(layer));
        Assert.Equal(BusState.Connected, bus.State);
    }
}
