using Solve.Core.Domain.Buses;

namespace Solve.Core.Tests.Buses;

public sealed class BusDescriptorValidationTests
{
    private static SerialBusDescriptor ValidSerial() => new(BusId.New(), "RS485-1", "COM3");

    private static TcpBusDescriptor ValidTcp() => new(BusId.New(), "PLC-01", "192.168.1.10", 502);

    private static IReadOnlyList<string> Codes(BusDescriptor descriptor)
        => descriptor.Validate().Select(e => e.Code).ToList();

    private static void AssertSingleError(BusDescriptor descriptor, string field)
        => Assert.Equal([BusErrors.DescriptorCodePrefix + field], Codes(descriptor));

    // ---------- 公共字段 ----------

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(ValidSerial().Validate());
        Assert.Empty(ValidTcp().Validate());
    }

    [Fact]
    public void Defaults_Are9600_8N1()
    {
        var serial = ValidSerial();

        Assert.Equal(9600, serial.BaudRate);
        Assert.Equal(8, serial.DataBits);
        Assert.Equal(SerialParity.None, serial.Parity);
        Assert.Equal(SerialStopBits.One, serial.StopBits);
    }

    [Fact]
    public void EmptyId_IsRejected()
        => AssertSingleError(ValidSerial() with { Id = default }, "Id");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_IsRejected(string? name)
        => AssertSingleError(ValidSerial() with { Name = name! }, "Name");

    [Fact]
    public void TooLongName_IsRejected()
        => AssertSingleError(ValidSerial() with { Name = new string('x', BusDescriptor.MaxNameLength + 1) }, "Name");

    [Fact]
    public void MultipleErrors_AreAllReported()
    {
        var descriptor = new SerialBusDescriptor(default, "", "") { BaudRate = 0 };

        Assert.Equal(
            ["Bus.Descriptor.Id", "Bus.Descriptor.Name", "Bus.Descriptor.PortName", "Bus.Descriptor.BaudRate"],
            Codes(descriptor));
    }

    [Fact]
    public void NullNestedConfig_IsRejected_NotThrown()
    {
        var descriptor = ValidTcp() with { Timeouts = null!, Reconnect = null! };

        Assert.Equal(["Bus.Descriptor.Timeouts", "Bus.Descriptor.Reconnect"], Codes(descriptor));
    }

    // ---------- 超时 ----------

    public static TheoryData<TimeSpan> InvalidTimeouts() => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(-5),
        Timeout.InfiniteTimeSpan,
        BusTimeouts.MaxTimeout + TimeSpan.FromTicks(1),
    };

    [Theory]
    [MemberData(nameof(InvalidTimeouts))]
    public void InvalidTimeout_IsRejected(TimeSpan value)
    {
        var timeouts = BusTimeouts.Default;

        AssertSingleError(ValidTcp() with { Timeouts = timeouts with { Connect = value } }, "Timeouts.Connect");
        AssertSingleError(ValidTcp() with { Timeouts = timeouts with { Read = value } }, "Timeouts.Read");
        AssertSingleError(ValidTcp() with { Timeouts = timeouts with { Write = value } }, "Timeouts.Write");
    }

    [Fact]
    public void TimeoutAtUpperBound_IsAccepted()
    {
        var timeouts = new BusTimeouts(BusTimeouts.MaxTimeout, BusTimeouts.MaxTimeout, BusTimeouts.MaxTimeout);

        Assert.Empty((ValidTcp() with { Timeouts = timeouts }).Validate());
    }

    // ---------- 重连策略 ----------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.99)]
    [InlineData(10.01)]
    public void InvalidMultiplier_IsRejected(double multiplier)
        => AssertSingleError(ValidTcp() with { Reconnect = ReconnectPolicy.Default with { Multiplier = multiplier } }, "Reconnect.Multiplier");

    [Fact]
    public void NonPositiveInitialDelay_IsRejected()
        => AssertSingleError(ValidTcp() with { Reconnect = ReconnectPolicy.Default with { InitialDelay = TimeSpan.Zero } }, "Reconnect.InitialDelay");

    [Fact]
    public void MaxDelayBelowInitial_IsRejected()
    {
        var policy = ReconnectPolicy.Default with { InitialDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(5) };

        AssertSingleError(ValidTcp() with { Reconnect = policy }, "Reconnect.MaxDelay");
    }

    [Fact]
    public void MaxDelayAboveLimit_IsRejected()
    {
        var policy = ReconnectPolicy.Default with { MaxDelay = ReconnectPolicy.MaxDelayLimit + TimeSpan.FromSeconds(1) };

        AssertSingleError(ValidTcp() with { Reconnect = policy }, "Reconnect.MaxDelay");
    }

    [Fact]
    public void NegativeMaxAttempts_IsRejected()
        => AssertSingleError(ValidTcp() with { Reconnect = ReconnectPolicy.Default with { MaxAttempts = -1 } }, "Reconnect.MaxAttempts");

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void ZeroOrUnlimitedMaxAttempts_IsAccepted(int? maxAttempts)
        => Assert.Empty((ValidTcp() with { Reconnect = ReconnectPolicy.Default with { MaxAttempts = maxAttempts } }).Validate());

    // ---------- 串口 ----------

    [Theory]
    [InlineData(SerialBusDescriptor.MinBaudRate - 1)]
    [InlineData(SerialBusDescriptor.MaxBaudRate + 1)]
    public void BaudRateOutOfRange_IsRejected(int baudRate)
        => AssertSingleError(ValidSerial() with { BaudRate = baudRate }, "BaudRate");

    [Fact]
    public void NonStandardBaudRate_IsAccepted()
        => Assert.Empty((ValidSerial() with { BaudRate = 250_000 }).Validate());

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public void DataBitsOutOfRange_IsRejected(int dataBits)
        => AssertSingleError(ValidSerial() with { DataBits = dataBits }, "DataBits");

    [Fact]
    public void UndefinedParity_IsRejected()
        => AssertSingleError(ValidSerial() with { Parity = (SerialParity)99 }, "Parity");

    [Fact]
    public void DefaultStopBits_IsRejected()
        => AssertSingleError(ValidSerial() with { StopBits = default }, "StopBits");

    [Theory]
    [InlineData(8, SerialStopBits.OnePointFive, false)]
    [InlineData(5, SerialStopBits.OnePointFive, true)]
    [InlineData(5, SerialStopBits.Two, false)]
    [InlineData(5, SerialStopBits.One, true)]
    [InlineData(7, SerialStopBits.Two, true)]
    public void StopBitsDataBitsCombination_FollowsUartConstraint(int dataBits, SerialStopBits stopBits, bool valid)
    {
        var errors = Codes(ValidSerial() with { DataBits = dataBits, StopBits = stopBits });

        if (valid)
            Assert.Empty(errors);
        else
            Assert.Equal(["Bus.Descriptor.StopBits"], errors);
    }

    // ---------- TCP ----------

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("plc-01.factory.local")]
    public void ValidHost_IsAccepted(string host)
        => Assert.Empty((ValidTcp() with { Host = host }).Validate());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad host")]
    public void InvalidHost_IsRejected(string? host)
        => AssertSingleError(ValidTcp() with { Host = host! }, "Host");

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void PortOutOfRange_IsRejected(int port)
        => AssertSingleError(ValidTcp() with { Port = port }, "Port");
}
