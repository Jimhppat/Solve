using System.IO.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.Hardware.Buses.Serial;
using Solve.Core.Domain.Buses;

namespace Solve.Core.Tests.Buses;

/// <summary>
/// 串口通道测试。不打开任何真实串口：只使用不以 COM 开头的非法端口名（.NET 在访问设备前即拒绝）。
/// 真实收发需在测试台上用实机或虚拟串口对验证。
/// </summary>
public sealed class SerialPortTransportTests
{
    private const string NonexistentPort = "SOLVE_TEST_NO_SUCH_PORT";

    private static SerialPortTransport Create(string portName = NonexistentPort)
        => new(new SerialBusDescriptor(BusId.New(), "RS485-Test", portName), NullLogger<SerialPortTransport>.Instance);

    [Theory]
    [InlineData(SerialParity.None, Parity.None)]
    [InlineData(SerialParity.Odd, Parity.Odd)]
    [InlineData(SerialParity.Even, Parity.Even)]
    [InlineData(SerialParity.Mark, Parity.Mark)]
    [InlineData(SerialParity.Space, Parity.Space)]
    public void Parity_MapsExplicitly(SerialParity domain, Parity expected)
        => Assert.Equal(expected, SerialMappings.ToPortParity(domain));

    [Theory]
    [InlineData(SerialStopBits.One, StopBits.One)]
    [InlineData(SerialStopBits.Two, StopBits.Two)]
    [InlineData(SerialStopBits.OnePointFive, StopBits.OnePointFive)]
    public void StopBits_MapsExplicitly(SerialStopBits domain, StopBits expected)
        => Assert.Equal(expected, SerialMappings.ToPortStopBits(domain));

    [Fact]
    public void UndefinedValues_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialMappings.ToPortParity((SerialParity)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialMappings.ToPortStopBits(default));
    }

    [Fact]
    public async Task InvalidPort_ReturnsOpenFailed_WithoutThrowing()
    {
        await using var transport = Create();

        var result = await transport.OpenAsync(CancellationToken.None);

        Assert.Equal(BusErrors.OpenFailedCode, result.Error.Code);
        Assert.Contains(NonexistentPort, result.Error.Message);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task IoBeforeOpen_ReturnsNotOpen()
    {
        await using var transport = Create();

        Assert.Equal(BusErrors.NotOpenCode, (await transport.ReadAsync(new byte[1], CancellationToken.None)).Error.Code);
        Assert.Equal(BusErrors.NotOpenCode, (await transport.WriteAsync(new byte[] { 1 }, CancellationToken.None)).Error.Code);
        Assert.Equal(BusErrors.NotOpenCode, (await transport.DiscardInputAsync(CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndRejectsFurtherUse()
    {
        var transport = Create();

        await transport.DisposeAsync();
        await transport.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.OpenAsync(CancellationToken.None).AsTask());
    }
}
