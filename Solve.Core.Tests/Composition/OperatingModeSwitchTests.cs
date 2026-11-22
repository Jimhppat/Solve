using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Settings;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Tests.Buses.TestSupport;
using Solve.Desktop.Composition;

namespace Solve.Core.Tests.Composition;

/// <summary>
/// 生产 / 仿真切换：只写配置库、重启生效。全部在仿真通道上运行，不触碰任何设备
/// </summary>
public sealed class OperatingModeSwitchTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private sealed class MemorySettings : IAppSettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool FailWrites { get; set; }

        /// <summary>
        /// 非空时写入成功但回读得到这个值，模拟写入未真正落盘
        /// </summary>
        public string? ReadBackOverride { get; set; }

        public int Writes { get; private set; }

        public Task<Result<string?>> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(Result<string?>.Success(ReadBackOverride ?? Values.GetValueOrDefault(key)));

        public Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            if (FailWrites)
                return Task.FromResult(Result.Failure(SettingsErrors.StorageFailed("磁盘写满")));

            Writes++;
            Values[key] = value;
            return Task.FromResult(Result.Success());
        }
    }

    private readonly MemorySettings _settings = new();
    private readonly BusManager _manager = new(
        new SimulatedTransportFactory(), new TestInterlock(), NullLoggerFactory.Instance,
        new BusRuntimeOptions { ShutdownTimeout = TimeSpan.FromSeconds(2) });

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    private OperatingModeSwitch Create(TransportMode current, bool hardwareRequested)
        => new(current, hardwareRequested, _settings, _manager, NullLogger<OperatingModeSwitch>.Instance);

    // 回环地址：即使误连也不会触达现场设备
    private static TcpBusDescriptor Tcp(string name = "PLC-01") => new(BusId.New(), name, "127.0.0.1", 50_502);

    private static async Task WaitForStateAsync(BusRuntime runtime, BusState expected)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (runtime.State != expected)
            await Task.Delay(5, guard.Token);
    }

    [Fact]
    public void CurrentMode_FollowsTransportMode()
    {
        Assert.Equal(OperatingMode.Simulation, Create(TransportMode.Simulation, true).CurrentMode);
        Assert.Equal(OperatingMode.Production, Create(TransportMode.Hardware, true).CurrentMode);
    }

    [Fact]
    public async Task ToProduction_WithHardwareArgument_SavesTrueAndPendsRestart()
    {
        var modeSwitch = Create(TransportMode.Simulation, hardwareRequested: true);

        var result = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(TransportModePolicy.EnabledValue, _settings.Values[TransportModePolicy.HardwareEnabledKey]);
        Assert.True(modeSwitch.IsRestartPending);
        // 保存的值与 --hardware 一起，重启后策略确实会落到实机
        var next = TransportModePolicy.Decide(true, Result<string?>.Success(_settings.Values[TransportModePolicy.HardwareEnabledKey]));
        Assert.Equal(TransportMode.Hardware, next.Mode);
    }

    [Fact]
    public async Task ToSimulation_SavesFalse()
    {
        _settings.Values[TransportModePolicy.HardwareEnabledKey] = TransportModePolicy.EnabledValue;
        var modeSwitch = Create(TransportMode.Hardware, hardwareRequested: true);

        var result = await modeSwitch.SaveAsync(OperatingMode.Simulation, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(TransportModePolicy.DisabledValue, _settings.Values[TransportModePolicy.HardwareEnabledKey]);
        Assert.True(modeSwitch.IsRestartPending);
    }

    [Fact]
    public async Task ToProduction_WithoutHardwareArgument_IsRejectedAndNothingWritten()
    {
        var modeSwitch = Create(TransportMode.Simulation, hardwareRequested: false);

        var result = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Mode.ProductionNotAllowed", result.Error.Code);
        Assert.NotNull(modeSwitch.ProductionBlockedReason);
        Assert.Equal(0, _settings.Writes);
        Assert.False(modeSwitch.IsRestartPending);
    }

    [Fact]
    public async Task SameMode_IsRejected()
    {
        var modeSwitch = Create(TransportMode.Simulation, hardwareRequested: true);

        var result = await modeSwitch.SaveAsync(OperatingMode.Simulation, CancellationToken.None);

        Assert.Equal("Mode.Unchanged", result.Error.Code);
        Assert.Equal(0, _settings.Writes);
    }

    [Fact]
    public async Task ConnectedBus_BlocksSwitch_UntilDisconnected()
    {
        var runtime = _manager.Register(Tcp("PLC-01")).Value;
        runtime.Connect();
        await WaitForStateAsync(runtime, BusState.Connected);
        var modeSwitch = Create(TransportMode.Simulation, hardwareRequested: true);

        var blocked = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);

        Assert.Equal("Mode.BusesActive", blocked.Error.Code);
        Assert.Contains("PLC-01", blocked.Error.Message);
        Assert.Equal(0, _settings.Writes);

        Assert.True((await runtime.DisconnectAsync(CancellationToken.None)).IsSuccess);
        await WaitForStateAsync(runtime, BusState.Disconnected);

        var allowed = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);
        Assert.True(allowed.IsSuccess, allowed.ToString());
    }

    [Fact]
    public async Task WriteFailure_KeepsModeAndAllowsRetry()
    {
        _settings.FailWrites = true;
        var modeSwitch = Create(TransportMode.Simulation, hardwareRequested: true);

        var failed = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);

        Assert.Equal(SettingsErrors.StorageFailedCode, failed.Error.Code);
        Assert.False(modeSwitch.IsRestartPending);

        _settings.FailWrites = false;
        Assert.True((await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task ReadBackMismatch_DoesNotPendRestart()
    {
        _settings.ReadBackOverride = "TRUE";
        var modeSwitch = Create(TransportMode.Simulation, hardwareRequested: true);

        var result = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);

        Assert.Equal("Mode.VerifyFailed", result.Error.Code);
        Assert.False(modeSwitch.IsRestartPending);
    }

    [Fact]
    public async Task AfterSave_FurtherRequestsAreRejected()
    {
        _settings.Values[TransportModePolicy.HardwareEnabledKey] = TransportModePolicy.EnabledValue;
        var modeSwitch = Create(TransportMode.Hardware, hardwareRequested: true);
        Assert.True((await modeSwitch.SaveAsync(OperatingMode.Simulation, CancellationToken.None)).IsSuccess);

        // 已保存为仿真、等待重启：再拨回生产也不能改写，重启后的模式就是操作员确认过的那一个
        var again = await modeSwitch.SaveAsync(OperatingMode.Production, CancellationToken.None);

        Assert.Equal("Mode.RestartPending", again.Error.Code);
        Assert.Equal(TransportModePolicy.DisabledValue, _settings.Values[TransportModePolicy.HardwareEnabledKey]);
    }
}
