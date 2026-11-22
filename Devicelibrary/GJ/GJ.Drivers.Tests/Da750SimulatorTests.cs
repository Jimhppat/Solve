using GJ.Drivers.Common;
using GJ.Drivers.Da750;
using Solve.Device.Sdk;

namespace GJ.Drivers.Tests;

/// <summary>
/// 仿真设备与本驱动的会话对跑：仿真模式下不接设备也能跑通轮询与命令
/// </summary>
public sealed class Da750SimulatorTests
{
    private const int Address = 3;

    private static readonly Da750Driver Driver = new(Da750Model.Da750_4_60A);

    private static (IDeviceSession Session, ISimulatedDevice Device) Pair(string von = "8")
    {
        var context = Da750Tests.Context(Address, von);
        return (Driver.CreateSession(context), Driver.CreateSimulator(context));
    }

    private static ExchangeStep Run(IExchange exchange, ISimulatedDevice device, int chunk = int.MaxValue)
        => ExchangeRunner.Execute(exchange, request => device.Respond(request), chunk).Final;

    private static ExchangeStep Command(IDeviceSession session, ISimulatedDevice device, string key, int channel, Dictionary<string, double>? arguments = null)
        => Run(session.BeginCommand(new DeviceCommand(key, arguments ?? new Dictionary<string, double>()) { Channel = channel }), device);

    private static double Sample(ExchangeStep step, string key, int channel)
        => Assert.IsType<ExchangeStep.Completed>(step).Samples.Single(s => s.Key == key && s.Channel == channel).Value;

    [Fact]
    public void PowerOn_AllChannelsUnloaded()
    {
        var (session, device) = Pair();

        var poll = Run(session.BeginPoll(), device);

        for (var channel = 1; channel <= 4; channel++)
        {
            Assert.Equal(0, Sample(poll, "current", channel));
            Assert.Equal(0, Sample(poll, "input", channel));
        }
    }

    [Fact]
    public void Commands_ChangeReadings()
    {
        var (session, device) = Pair();

        Assert.IsType<ExchangeStep.Completed>(Command(session, device, "load.cc", 1, new() { ["current"] = 10 }));
        Assert.IsType<ExchangeStep.Completed>(Command(session, device, "load.cv", 2, new() { ["voltage"] = 5 }));
        var loaded = Run(session.BeginPoll(), device, chunk: 3);

        Assert.Equal((12.0, 10.0, 120.0, 1.0), (Sample(loaded, "voltage", 1), Sample(loaded, "current", 1), Sample(loaded, "power", 1), Sample(loaded, "input", 1)));
        Assert.Equal((5.0, Da750Simulator.CvCurrent), (Sample(loaded, "voltage", 2), Sample(loaded, "current", 2)));

        Assert.IsType<ExchangeStep.Completed>(Command(session, device, "load.off", 1));
        var unloaded = Run(session.BeginPoll(), device);

        Assert.Equal(0, Sample(unloaded, "current", 1));
        Assert.Equal(Da750Simulator.CvCurrent, Sample(unloaded, "current", 2));
    }

    [Fact]
    public void VonAboveSourceVoltage_DoesNotLoad()
    {
        var (session, device) = Pair(von: "15");

        Command(session, device, "load.cc", 1, new() { ["current"] = 10 });

        Assert.Equal(0, Sample(Run(session.BeginPoll(), device), "current", 1));
    }

    [Fact]
    public void RequestForOtherAddress_IsNotAnswered()
        => Assert.Null(Pair().Device.Respond(Da750Protocol.ReadValuesRequest(Address + 1)));

    [Fact]
    public void NonProtocolBytes_AreNotAnswered()
        => Assert.Null(Pair().Device.Respond("*IDN?\n"u8));

    [Fact]
    public void BadChecksum_IsAnsweredWithF1()
    {
        var request = Da750Protocol.ReadValuesRequest(Address);
        request[^2] ^= 0x01;

        var reply = Pair().Device.Respond(request);

        Assert.NotNull(reply);
        Assert.Equal(Ers272Code.ChecksumError, Ers272Frame.CodeOf(reply));
    }

    [Fact]
    public void UnknownCommand_IsAnsweredWithF3()
    {
        var reply = Pair().Device.Respond(Ers272Frame.EncodeRequest(Address, 0x09, []));

        Assert.NotNull(reply);
        Assert.Equal(Ers272Code.InvalidCommand, Ers272Frame.CodeOf(reply));
    }
}
