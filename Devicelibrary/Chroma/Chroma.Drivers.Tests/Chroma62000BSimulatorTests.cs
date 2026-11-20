using System.Text;
using Chroma.Drivers.Psu62000B;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Tests;

/// <summary>
/// 仿真设备与驱动自己的会话对接：驱动的轮询与命令在仿真设备上能完整跑通
/// </summary>
public sealed class Chroma62000BSimulatorTests
{
    private static readonly Chroma62000BDriver Driver = new(Chroma62000BModel.M62015B_15_90);

    private static readonly DeviceContext Context = new(1, LinkKind.Tcp,
        new DeviceParameters(new Dictionary<string, string> { [Chroma62000BDriver.CommandDelayKey] = "0" }));

    private readonly ISimulatedDevice _simulator = ((IDeviceSimulatorProvider)Driver).CreateSimulator(Context);
    private readonly IDeviceSession _session = Driver.CreateSession(Context);

    private string? Handle(string command)
    {
        var reply = _simulator.Respond(Encoding.ASCII.GetBytes(command + "\n"));
        return reply is null ? null : Encoding.ASCII.GetString(reply);
    }

    private ExchangeStep Poll() => ExchangeRunner.Execute(_session.BeginPoll(), Handle).Final;

    private ExchangeStep Command(string key)
        => ExchangeRunner.Execute(_session.BeginCommand(new DeviceCommand(key, new Dictionary<string, double>())), Handle).Final;

    private static double Sample(ExchangeStep step, string key)
        => Assert.IsType<ExchangeStep.Completed>(step).Samples.Single(s => s.Key == key).Value;

    [Fact]
    public void Poll_PassesIdentityCheck_OutputOffReadsZero()
    {
        var step = Poll();

        Assert.Equal(0.0, Sample(step, Chroma62000BDriver.VoltageKey));
        Assert.Equal(0.0, Sample(step, Chroma62000BDriver.CurrentKey));
        Assert.Equal(0.0, Sample(step, Chroma62000BDriver.OutputKey));
    }

    [Fact]
    public void OutputOn_IsConfirmedByReadback_ThenPollReadsRatedFractions()
    {
        Assert.IsType<ExchangeStep.Completed>(Command(Chroma62000BDriver.OutputOnKey));

        var step = Poll();
        Assert.Equal(12.0, Sample(step, Chroma62000BDriver.VoltageKey), precision: 6);
        Assert.Equal(9.0, Sample(step, Chroma62000BDriver.CurrentKey), precision: 6);
        Assert.Equal(108.0, Sample(step, Chroma62000BDriver.PowerKey), precision: 6);
        Assert.Equal(1.0, Sample(step, Chroma62000BDriver.OutputKey));

        Assert.IsType<ExchangeStep.Completed>(Command(Chroma62000BDriver.OutputOffKey));
        Assert.Equal(0.0, Sample(Poll(), Chroma62000BDriver.OutputKey));
    }

    [Theory]
    [InlineData("CONF:OUTP ON\n")]
    [InlineData("SYST:ERR?\n")]
    [InlineData("*IDN?")]
    [InlineData("*IDN?\nFETC:VOLT?\n")]
    public void SetCommand_UnknownCommand_OrIncompleteLine_GetsNoReply(string request)
        => Assert.Null(_simulator.Respond(Encoding.ASCII.GetBytes(request)));

    [Fact]
    public void NonAsciiRequest_GetsNoReply()
        => Assert.Null(_simulator.Respond([0xAA, 0x01, 0x01, 0xAC]));

    [Fact]
    public void Keywords_AreCaseInsensitive_AndCrIsTolerated()
        => Assert.Equal("OFF\n", Encoding.ASCII.GetString(_simulator.Respond("conf:outp?\r\n"u8)!));
}
