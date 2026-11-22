using System.Reflection;
using Solve.Adapters.Persistence.Sqlite;
using Solve.Core.Application.Devices.Commands;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Composition;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.Composition;

/// <summary>
/// 整条仿真链路的回归：用源码目录 Devicelibrary/ 下编译产出的真实驱动包（与主程序从源码运行时载入的是同一份），
/// 在仿真模式的组合根里走操作员的完整步骤：信任驱动包 → 建档 → 建总线 → 挂模块 → 接线 → 连接总线 → 下发命令 → 安全停止。
/// </summary>
/// <remarks>
/// <para>链路：命令服务 → 执行器 → 总线通道锁 → 仿真通道 → 仿真设备分派 → 驱动包的仿真设备 → 应答 → 驱动会话解码 → 模块读数。
/// 读数随命令变化、安全停止后归零，说明请求确实经过了驱动包的仿真设备，而不是停在内核的某一层。</para>
/// <para>只用仿真通道与回环地址，不触碰任何真实设备。内核不编译驱动：本测试与主程序一样只读取已经编译好的驱动包，
/// 要先单独编译驱动库（dotnet build Devicelibrary/Devicelibrary.slnx）。找不到驱动包时失败而不是跳过：安全停止链路的回归不能悄悄不跑。</para>
/// </remarks>
[Collection(LoggingPipelineCollection.Name)]
public sealed class DriverPackageSimulationTests : IAsyncLifetime
{
    private static readonly DriverId Da750 = DriverId.Of("gj.da750-4-60a");
    private static readonly DriverId Chroma62015B = DriverId.Of("chroma.62015b-15-90");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private AppHost? _host;

    private string DatabasePath => Path.Combine(_directory, "config", "solve.db");

    /// <summary>
    /// 源码中的驱动库目录，由测试项目在编译时写入程序集元数据（与输出目录在哪里无关）
    /// </summary>
    private static string SourceLibrary
        => typeof(DriverPackageSimulationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DeviceLibrarySource").Value!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();

        SqliteDatabase.ClearPool(new SqliteStorageOptions(DatabasePath));
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // ==================== GJ DA750（多通道电子负载，RS-485 多台同总线） ====================

    [Fact]
    public async Task Da750_Simulation_LoadsAndUnloadsThroughWholeChain()
    {
        var host = await StartAsync("gj.drivers");
        var layer = await PlanLayerAsync(host);

        var bus = (await host.BusService!.CreateBusAsync(new SerialBusDescriptor(BusId.New(), "1# RS-485", "COM7"), CancellationToken.None)).Value;
        var added = await host.Devices!.AddBatchAsync(new DeviceBatchRequest(bus.Id, Da750, "负载", 1, 1), CancellationToken.None);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        var load = added.Value[0];
        var channel = new ChannelRef(load.Id, "load", 1);
        await WireAsync(host, layer, channel);

        // 连接仿真总线：驱动包的仿真设备应答轮询，模块上线，接线就绪；上电默认全部卸载
        Assert.True(bus.Connect().IsSuccess);
        await WaitUntilAsync(() => host.Wiring!.CheckReady(layer).IsSuccess);
        await WaitUntilAsync(() => Reading(host, layer, load.Id, "current", 1) is { } idle && Math.Abs(idle) < 0.05);

        // 恒流带载 10 A：仿真模式下输出动作不需要确认（实机需要），读回电流等于设定、电压为仿真源的 12 V
        var command = host.Commands!.Prepare(new DeviceCommandRequest(layer, ChannelKind.Load, null, "load.cc")
        {
            Channel = channel,
            Arguments = new Dictionary<string, double> { ["current"] = 10 },
        });
        Assert.True(command.IsSuccess, command.IsFailure ? command.Error.Message : null);
        Assert.False(command.Value.IsHardware);
        Assert.False(command.Value.RequiresConfirmation);
        var executed = await host.Commands.ExecuteAsync(command.Value, confirmed: false, CancellationToken.None);
        Assert.True(executed.IsSuccess, executed.IsFailure ? executed.Error.Message : null);

        await WaitUntilAsync(() => Reading(host, layer, load.Id, "current", 1) is { } on && Math.Abs(on - 10) < 0.05);
        Assert.Equal(12, Reading(host, layer, load.Id, "voltage", 1)!.Value, 2);
        Assert.Equal(1, Reading(host, layer, load.Id, "input", 1)!.Value, 2);

        // 按位置安全停止这一路：驱动的安全状态报文同样由仿真设备执行，读数回到卸载
        var stopped = await host.Commands.SafeStopAsync(
            new DeviceSafeStopRequest(layer, ChannelKind.Load, null, SafeStopScope.Channel) { Channel = channel }, CancellationToken.None);
        Assert.True(stopped.IsSuccess, stopped.IsFailure ? stopped.Error.Message : null);
        await WaitUntilAsync(() => Reading(host, layer, load.Id, "current", 1) is { } off && Math.Abs(off) < 0.05);
        Assert.Equal(0, Reading(host, layer, load.Id, "input", 1)!.Value, 2);
        Assert.Equal(BusState.Connected, bus.State);
    }

    // ==================== Chroma 62015B（单路程控电源，SCPI 字节流） ====================

    [Fact]
    public async Task Chroma62015B_Simulation_OutputOnOff_AndEmergencyStopTurnsItOff()
    {
        var host = await StartAsync("chroma.drivers");
        var layer = await PlanLayerAsync(host);

        // 回环地址 + 未使用端口：仿真模式下根本不建立 TCP 连接，即使误用也到不了现场设备
        var bus = (await host.BusService!.CreateBusAsync(new TcpBusDescriptor(BusId.New(), "电源网口", "127.0.0.1", 50_025), CancellationToken.None)).Value;
        var added = await host.Devices!.AddBatchAsync(new DeviceBatchRequest(bus.Id, Chroma62015B, "电源", 1, 1), CancellationToken.None);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        var psu = added.Value[0];
        var channel = new ChannelRef(psu.Id, "output", 1);
        await WireAsync(host, layer, channel);

        Assert.True(bus.Connect().IsSuccess);
        await WaitUntilAsync(() => host.Wiring!.CheckReady(layer).IsSuccess);

        // 打开输出：仿真电源输出额定 15 V 的 80%
        await ExecuteAsync(host, layer, ChannelKind.PowerSupply, channel, "output.on");
        await WaitUntilAsync(() => Reading(host, layer, psu.Id, "voltage", 0) is { } on && Math.Abs(on - 12) < 0.05);
        Assert.Equal(1, Reading(host, layer, psu.Id, "output", 0)!.Value, 2);

        // 关闭输出
        await ExecuteAsync(host, layer, ChannelKind.PowerSupply, channel, "output.off");
        await WaitUntilAsync(() => Reading(host, layer, psu.Id, "voltage", 0) is { } off && Math.Abs(off) < 0.05);

        // 再打开后急停：总线锁存安全停止，跳闸时驱动的安全状态（关输出）经仿真通道下发给仿真设备
        await ExecuteAsync(host, layer, ChannelKind.PowerSupply, channel, "output.on");
        await WaitUntilAsync(() => Reading(host, layer, psu.Id, "output", 0) is { } again && Math.Abs(again - 1) < 0.01);
        Assert.Equal(1, host.EmergencyStop("仿真链路测试：急停"));
        await bus.SafeStopSettled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BusState.SafeStop, bus.State);

        // 复位并重新连接后读到的是"输出关闭"：证明安全状态报文确实到达了驱动包的仿真设备
        Assert.True(bus.Reset().IsSuccess);
        Assert.True(bus.Connect().IsSuccess);
        await WaitUntilAsync(() => bus.State == BusState.Connected);
        await WaitUntilAsync(() => Reading(host, layer, psu.Id, "output", 0) is { } after && Math.Abs(after) < 0.01);
        Assert.Equal(0, Reading(host, layer, psu.Id, "voltage", 0)!.Value, 2);
    }

    // ==================== 公共步骤 ====================

    /// <summary>
    /// 把源码驱动库里编译好的驱动包复制到临时驱动库，以仿真模式启动组合根并信任这些包
    /// </summary>
    private async Task<AppHost> StartAsync(params string[] packageIds)
    {
        var library = Path.Combine(_directory, "DeviceLibrary");
        foreach (var id in packageIds)
        {
            var source = Path.Combine(SourceLibrary, id);
            Assert.True(File.Exists(Path.Combine(source, SdkInfo.ManifestFileName)),
                $"找不到编译好的驱动包 {source}：请先编译驱动库（dotnet build Devicelibrary/Devicelibrary.slnx）");

            var target = Directory.CreateDirectory(Path.Combine(library, id)).FullName;
            foreach (var file in Directory.EnumerateFiles(source).Where(f => !Path.GetFileName(f).StartsWith('.')))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        _host = AppHost.Create(new DesktopOptions(false, DatabasePath, Path.Combine(_directory, "logs"), DeviceLibraryDirectory: library));
        var report = await _host.Start();
        Assert.True(report.Succeeded, report.Fatal?.ToString());
        Assert.Equal(TransportMode.Simulation, report.Mode!.Mode);

        foreach (var id in packageIds)
        {
            var pending = _host.DeviceLibrary!.Packages.Single(p => p.Id == id);
            var approved = await _host.DeviceLibrary.ApproveAsync(pending.Id, pending.Fingerprint, CancellationToken.None);
            Assert.True(approved.IsSuccess, approved.IsFailure ? approved.Error.Message : null);
            Assert.Equal(Solve.Core.Application.DeviceLibrary.DriverPackageStatus.Loaded, approved.Value.Single(p => p.Id == id).Status);
        }

        return _host;
    }

    /// <summary>
    /// 建档：1 柜 × 1 层 × 1 槽 × 1 治具 × 1 产品，返回层位
    /// </summary>
    private static async Task<TopologyNodeId> PlanLayerAsync(AppHost host)
    {
        var topology = host.Topology!;
        var saved = await topology.SaveAsync(topology.PlanLayout("老化房", new TopologyLayout(1, 1, 1, 1, 1)).Value, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        return topology.Current!.GetNode(TopologyAddress.Parse("C01-L01")).Value.Id;
    }

    /// <summary>
    /// 把模块通道接到位置。模块绑定驱动在后台完成，绑定就绪前接线计划不成立
    /// </summary>
    private static async Task WireAsync(AppHost host, TopologyNodeId node, ChannelRef channel)
    {
        var wiring = host.Wiring!.Wiring;
        await WaitUntilAsync(() => wiring.PlanAdd(node, channel, null, null).IsSuccess);
        var applied = await wiring.ApplyAsync(wiring.PlanAdd(node, channel, null, null).Value, CancellationToken.None);
        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);
    }

    private static async Task ExecuteAsync(AppHost host, TopologyNodeId node, ChannelKind kind, ChannelRef channel, string commandKey)
    {
        var plan = host.Commands!.Prepare(new DeviceCommandRequest(node, kind, null, commandKey) { Channel = channel });
        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : null);
        var executed = await host.Commands.ExecuteAsync(plan.Value, confirmed: false, CancellationToken.None);
        Assert.True(executed.IsSuccess, executed.IsFailure ? executed.Error.Message : null);
    }

    /// <summary>
    /// 位置上某模块最近一次轮询的读数（工程值）；没有读数时为 null
    /// </summary>
    private static double? Reading(AppHost host, TopologyNodeId node, DeviceId device, string key, int channel)
        => host.Wiring!.Inspect(node)
            .Select(w => w.Module)
            .FirstOrDefault(m => m?.DeviceId == device)?.Points
            .Where(p => p.Key == key && p.Channel == channel)
            .Select(p => (double?)p.Value)
            .FirstOrDefault();

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 50)
            await Task.Delay(50);

        Assert.True(condition(), "等待超时");
    }
}
