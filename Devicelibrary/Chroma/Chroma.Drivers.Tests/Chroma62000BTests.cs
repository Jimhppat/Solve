using System.Text;
using Chroma.Drivers.Common;
using Chroma.Drivers.Psu62000B;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Tests;

public sealed class Chroma62000BTests
{
    /// <summary>
    /// 按手册示例响应的模拟设备
    /// </summary>
    private sealed class FakeChroma
    {
        public string Identity { get; set; } = "CHROMA 62015B-15-90,01.00,2005/07/14";
        public string Voltage { get; set; } = "8.12";
        public string Current { get; set; } = "3.15";
        public int Alarms { get; set; }
        public bool OutputOn { get; set; }

        /// <summary>
        /// 为 true 时忽略输出开关命令（模拟远程未生效）
        /// </summary>
        public bool IgnoreOutputCommands { get; set; }

        public bool Silent { get; set; }

        public string LineEnding { get; set; } = "\n";

        public string? Handle(string command)
        {
            if (Silent)
                return null;

            switch (command)
            {
                case "CONF:OUTP ON" or "CONF:OUTP OFF":
                    if (!IgnoreOutputCommands)
                        OutputOn = command.EndsWith("ON", StringComparison.Ordinal);
                    return null;
            }

            var text = command switch
            {
                "*IDN?" => Identity,
                "FETC:VOLT?" => Voltage,
                "FETC:CURR?" => Current,
                "FETC:STAT?" => $"{(1 << 12) | (OutputOn ? 1 << 13 : 0)}, {Alarms}",
                "CONF:OUTP?" => OutputOn ? "ON" : "OFF",
                _ => null,
            };
            return text is null ? null : text + LineEnding;
        }
    }

    private static readonly Chroma62000BDriver Driver = new(Chroma62000BModel.M62015B_15_90);

    private readonly FakeChroma _device = new();

    private static IDeviceSession NewSession(string delayMs = "100")
        => Driver.CreateSession(new DeviceContext(1, LinkKind.Serial,
            new DeviceParameters(new Dictionary<string, string> { [Chroma62000BDriver.CommandDelayKey] = delayMs })));

    private ExchangeRunner.Run Poll(IDeviceSession session, int chunk = int.MaxValue)
        => ExchangeRunner.Execute(session.BeginPoll(), _device.Handle, chunk);

    private ExchangeRunner.Run Command(IDeviceSession session, string key)
        => ExchangeRunner.Execute(session.BeginCommand(new DeviceCommand(key, new Dictionary<string, double>())), _device.Handle);

    private static double Sample(ExchangeStep step, string key)
        => Assert.IsType<ExchangeStep.Completed>(step).Samples.Single(s => s.Key == key).Value;

    // ---------- 声明 ----------

    [Fact]
    public void Info_IsValid_AndDeclaresPowerSupply()
    {
        Assert.Empty(Driver.Info.Validate());
        Assert.Equal("chroma.62015b-15-90", Driver.Info.Id);
        Assert.Equal(DeviceCategory.PowerAndLoad, Driver.Info.Category);

        // 字节流封装不带地址：每条总线只能挂一台
        Assert.Equal((1, 1), (Driver.Info.MinAddress, Driver.Info.MaxAddress));
    }

    [Fact]
    public void Package_ListsDriver()
        => Assert.Equal(["chroma.62015b-15-90"], new ChromaDriverPackage().Drivers.Select(d => d.Info.Id));

    [Fact]
    public void Package_DependsOnlyOnSdkAndFramework()
    {
        var references = typeof(ChromaDriverPackage).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Solve.", StringComparison.Ordinal) && r != "Solve.Device.Sdk");
        Assert.All(references, r => Assert.True(
            r == "Solve.Device.Sdk" || r.StartsWith("System", StringComparison.Ordinal) || r == "netstandard",
            $"驱动包引用了白名单外的程序集 {r}"));
    }

    // ---------- 轮询 ----------

    [Fact]
    public void FirstPoll_VerifiesIdentity_ThenReadsAll()
    {
        var run = Poll(NewSession());

        Assert.Equal(["*IDN?\n", "FETC:VOLT?\n", "FETC:CURR?\n", "FETC:STAT?\n"], run.Commands);
        Assert.Equal(8.12, Sample(run.Final, "voltage"), 6);
        Assert.Equal(3.15, Sample(run.Final, "current"), 6);
        Assert.Equal(8.12 * 3.15, Sample(run.Final, "power"), 6);
        Assert.Equal(0, Sample(run.Final, "output"));
    }

    [Fact]
    public void NextPoll_AfterSuccess_SkipsIdentity()
    {
        var session = NewSession();
        Poll(session);

        var second = Poll(session);

        Assert.Equal(["FETC:VOLT?\n", "FETC:CURR?\n", "FETC:STAT?\n"], second.Commands);
    }

    [Fact]
    public void NextPoll_AfterTimeout_VerifiesIdentityAgain()
    {
        var session = NewSession();
        Poll(session);
        _device.Silent = true;
        Assert.IsType<ExchangeStep.Failed>(Poll(session).Final);
        _device.Silent = false;

        var recovered = Poll(session);

        Assert.Equal("*IDN?\n", recovered.Commands[0]);
        Assert.IsType<ExchangeStep.Completed>(recovered.Final);
    }

    [Theory]
    [InlineData("CHROMA,62015B-15-90,01.00,2005/07/14")]
    [InlineData("chroma 62015b-15-90,01.00,2005/07/14")]
    public void Identity_AcceptsManualAndStandardForms(string identity)
    {
        _device.Identity = identity;

        Assert.IsType<ExchangeStep.Completed>(Poll(NewSession()).Final);
    }

    [Fact]
    public void Identity_OtherModel_IsConfigurationError_AndReadsNothing()
    {
        _device.Identity = "CHROMA 62015B-80-18,01.00,2005/07/14";

        var run = Poll(NewSession());

        var failed = Assert.IsType<ExchangeStep.Failed>(run.Final);
        Assert.Equal(FailureKind.Configuration, failed.Kind);
        Assert.Contains("62015B-80-18", failed.Reason);
        Assert.Single(run.Sent);
    }

    [Fact]
    public void Identity_Garbled_IsTransient()
    {
        _device.Identity = "#@!";

        Assert.Equal(FailureKind.Transient, Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final).Kind);
    }

    [Fact]
    public void OutputOn_ReportedFromStatusBit13()
    {
        _device.OutputOn = true;

        Assert.Equal(1, Sample(Poll(NewSession()).Final, "output"));
    }

    [Fact]
    public void Alarm_IsDeviceFault_WithNamedBits()
    {
        _device.Alarms = (1 << 5) | (1 << 15);

        var failed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final);

        Assert.Equal(FailureKind.Device, failed.Kind);
        Assert.Contains("模块过压保护（硬件）", failed.Reason);
        Assert.Contains("0x8020", failed.Reason);
    }

    [Theory]
    [InlineData("19.5")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("")]
    public void ImplausibleVoltage_IsTransient_AndStopsPoll(string voltage)
    {
        _device.Voltage = voltage;

        var run = Poll(NewSession());

        Assert.Equal(FailureKind.Transient, Assert.IsType<ExchangeStep.Failed>(run.Final).Kind);
        Assert.Equal(2, run.Sent.Count);
    }

    [Theory]
    [InlineData("4096")]
    [InlineData("4096, 0, 1")]
    [InlineData("-1, 0")]
    [InlineData("70000, 0")]
    public void MalformedStatus_IsTransient(string status)
    {
        var exchange = new PollExchange(Chroma62000BModel.M62015B_15_90, verifyIdentity: false);
        var run = ExchangeRunner.Execute(exchange, c => c == "FETC:STAT?" ? status + "\n" : _device.Handle(c));

        Assert.Equal(FailureKind.Transient, Assert.IsType<ExchangeStep.Failed>(run.Final).Kind);
        Assert.False(exchange.Succeeded);
    }

    [Fact]
    public void Poll_ToleratesSplitPackets_AndCrLf()
    {
        _device.LineEnding = "\r\n";

        Assert.IsType<ExchangeStep.Completed>(Poll(NewSession(), chunk: 1).Final);
    }

    // ---------- 拆帧 ----------

    [Fact]
    public void Match_TrailingBytes_AreNotPartOfFrame()
    {
        var status = ScpiText.MatchLine("8.12\nGARBAGE"u8, out var length);

        Assert.Equal(FrameStatus.Complete, status);
        Assert.Equal(5, length);
    }

    [Fact]
    public void Match_NonAscii_IsInvalid()
        => Assert.Equal(FrameStatus.Invalid, ScpiText.MatchLine([0x38, 0xFF, 0x0A], out _));

    [Fact]
    public void Match_TooLongWithoutLineFeed_IsInvalid()
    {
        var noise = Encoding.ASCII.GetBytes(new string('1', ScpiText.MaxLineLength));

        Assert.Equal(FrameStatus.Invalid, ScpiText.MatchLine(noise, out _));
        Assert.Equal(FrameStatus.NeedMoreData, ScpiText.MatchLine(noise.AsSpan(0, 10), out _));
    }

    // ---------- 命令 ----------

    [Fact]
    public void OutputOn_SendsCommandWithoutResponse_ThenVerifies()
    {
        var session = NewSession(delayMs: "250");
        Poll(session);

        var run = Command(session, Chroma62000BDriver.OutputOnKey);

        Assert.Equal(["CONF:OUTP ON\n", "CONF:OUTP?\n"], run.Commands);
        Assert.False(run.Sent[0].ExpectsResponse);
        Assert.Equal(TimeSpan.FromMilliseconds(250), run.Sent[0].DelayAfter);
        Assert.IsType<ExchangeStep.Completed>(run.Final);
        Assert.True(_device.OutputOn);
    }

    [Fact]
    public void Command_OnFreshSession_VerifiesIdentityFirst()
    {
        var run = Command(NewSession(), Chroma62000BDriver.OutputOffKey);

        Assert.Equal(["*IDN?\n", "CONF:OUTP OFF\n", "CONF:OUTP?\n"], run.Commands);
    }

    [Fact]
    public void Command_ToWrongModel_SendsNoOutputCommand()
    {
        _device.Identity = "CHROMA 62015B-80-18,01.00,2005/07/14";

        var run = Command(NewSession(), Chroma62000BDriver.OutputOnKey);

        Assert.Equal(["*IDN?\n"], run.Commands);
        Assert.False(_device.OutputOn);
    }

    [Fact]
    public void OutputCommand_NotTakingEffect_IsDeviceFault()
    {
        _device.IgnoreOutputCommands = true;

        var failed = Assert.IsType<ExchangeStep.Failed>(Command(NewSession(), Chroma62000BDriver.OutputOnKey).Final);

        Assert.Equal(FailureKind.Device, failed.Kind);
        Assert.Contains("没有生效", failed.Reason);
    }

    // ---------- 安全状态（SDK 1.3） ----------

    private ExchangeRunner.Run SafeState(IDeviceSession session, string? group = null, int channel = 0)
        => ExchangeRunner.Execute(Assert.IsAssignableFrom<ISafeStateProvider>(session).BeginSafeState(group, channel), _device.Handle);

    [Fact]
    public void SafeState_OutputChannel_SameAsWholeModule()
    {
        _device.OutputOn = true;

        var run = SafeState(NewSession(), Chroma62000BDriver.OutputChannelKey, 1);

        Assert.Equal(["CONF:OUTP OFF\n", "CONF:OUTP?\n"], run.Commands);
        Assert.False(_device.OutputOn);
    }

    [Theory]
    [InlineData("output", 2)]
    [InlineData("load", 1)]
    [InlineData(null, 1)]
    public void SafeState_UndeclaredTarget_IsConfigurationError_WithoutSending(string? group, int channel)
    {
        var run = SafeState(NewSession(), group, channel);

        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeStep.Failed>(run.Final).Kind);
        Assert.Empty(run.Sent);
    }

    [Fact]
    public void SafeState_TurnsOutputOff_WithoutIdentity_ThenVerifies()
    {
        _device.OutputOn = true;

        // 新会话（型号从未确认）也直接关输出：跳闸时不能让型号确认挡住关输出
        var run = SafeState(NewSession(delayMs: "250"));

        Assert.Equal(["CONF:OUTP OFF\n", "CONF:OUTP?\n"], run.Commands);
        Assert.Equal("CONF:OUTP OFF\n"u8.ToArray(), run.Sent[0].Request.ToArray());
        Assert.False(run.Sent[0].ExpectsResponse);
        Assert.Equal(TimeSpan.FromMilliseconds(250), run.Sent[0].DelayAfter);
        Assert.IsType<ExchangeStep.Completed>(run.Final);
        Assert.False(_device.OutputOn);
    }

    [Fact]
    public void SafeState_NotTakingEffect_IsDeviceFault()
    {
        _device.OutputOn = true;
        _device.IgnoreOutputCommands = true;

        var failed = Assert.IsType<ExchangeStep.Failed>(SafeState(NewSession()).Final);

        Assert.Equal(FailureKind.Device, failed.Kind);
    }

    [Fact]
    public void SafeState_NextExchange_VerifiesIdentityAgain()
    {
        var session = NewSession();
        Poll(session);
        SafeState(session);

        // 安全状态没有确认型号：复位重连后的第一次交互重新确认（跳闸期间设备可能被更换）
        var run = Poll(session);

        Assert.Equal("*IDN?\n", run.Commands[0]);
    }

    [Fact]
    public void UnknownCommand_IsConfigurationError_WithoutSending()
    {
        var run = Command(NewSession(), "voltage.set");

        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeStep.Failed>(run.Final).Kind);
        Assert.Empty(run.Sent);
    }
}
