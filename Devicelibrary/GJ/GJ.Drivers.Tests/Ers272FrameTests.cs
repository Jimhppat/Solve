using GJ.Drivers.Common;
using Solve.Device.Sdk;

namespace GJ.Drivers.Tests;

/// <summary>
/// ERS272 帧的编码与成帧：报文向量按旧驱动 CalDataFromERS272Cmd / CalCheckSum 的算法手算
/// </summary>
public sealed class Ers272FrameTests
{
    private const int Address = 1;

    [Fact]
    public void Encode_ReadValues_MatchesLegacyAlgorithm()
    {
        // 校验和 = 01 + 0F + 03 = 0x0013
        Assert.Equal([0xEE, 0x01, 0x0F, 0x03, 0x00, 0x13, 0xEF], Ers272Frame.EncodeRequest(Address, 0x0F, []));
    }

    [Fact]
    public void Encode_ReplacesStartAndEndBytesInInfo_AndChecksumsTheReplacedBytes()
    {
        // INFO EE EF 10 → ED ED 10；校验和 = 01 + 41 + 06 + ED + ED + 10 = 0x0232
        Assert.Equal(
            [0xEE, 0x01, 0x41, 0x06, 0xED, 0xED, 0x10, 0x02, 0x32, 0xEF],
            Ers272Frame.EncodeRequest(Address, 0x41, [0xEE, 0xEF, 0x10]));
    }

    [Fact]
    public void Encode_ReplacesStartAndEndBytesInChecksum()
    {
        // 校验和 = 01 + 0F + 04 + DB = 0x00EF，低字节 EF → ED
        Assert.Equal([0xEE, 0x01, 0x0F, 0x04, 0xDB, 0x00, 0xED, 0xEF], Ers272Frame.EncodeRequest(Address, 0x0F, [0xDB]));
    }

    [Fact]
    public void Encode_RejectsAddressOutsideProtocolRange()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Ers272Frame.EncodeRequest(128, 0x0F, []));

    [Fact]
    public void Match_CompleteFrame_ExcludesTrailingBytes()
    {
        var frame = Ers272Frame.EncodeResponse(Address, Ers272Code.Ok, [0x01, 0x02]);
        byte[] received = [.. frame, 0x55, 0xEE];

        Assert.Equal(FrameStatus.Complete, Ers272Frame.Match(received, Address, 2, strict: true, out var length));
        Assert.Equal(frame.Length, length);
    }

    [Fact]
    public void Match_ByteByByte_NeedsMoreDataUntilEnd()
    {
        var frame = Ers272Frame.EncodeResponse(Address, Ers272Code.Ok, [0x01, 0x02]);

        for (var i = 1; i < frame.Length; i++)
            Assert.Equal(FrameStatus.NeedMoreData, Ers272Frame.Match(frame.AsSpan(0, i), Address, 2, strict: true, out _));
        Assert.Equal(FrameStatus.Complete, Ers272Frame.Match(frame, Address, 2, strict: true, out _));
    }

    [Fact]
    public void Match_ErrorResponseWithoutInfo_IsComplete()
    {
        var frame = Ers272Frame.EncodeResponse(Address, Ers272Code.InvalidCommand, []);

        Assert.Equal(FrameStatus.Complete, Ers272Frame.Match(frame, Address, 65, strict: true, out var length));
        Assert.Equal(Ers272Frame.Overhead, length);
    }

    [Fact]
    public void Match_BadChecksum_IsInvalidOnlyWhenStrict()
    {
        var frame = Ers272Frame.EncodeResponse(Address, Ers272Code.Ok, [0x01, 0x02]);
        frame[^2] ^= 0x01;

        Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match(frame, Address, 2, strict: true, out _));
        Assert.Equal(FrameStatus.Complete, Ers272Frame.Match(frame, Address, 2, strict: false, out _));
    }

    [Fact]
    public void Match_BadLengthField_IsInvalidOnlyWhenStrict()
    {
        var frame = Ers272Frame.EncodeResponse(Address, Ers272Code.Ok, [0x01, 0x02]);
        frame[3] = 0x10;

        Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match(frame, Address, 2, strict: true, out _));
        Assert.Equal(FrameStatus.Complete, Ers272Frame.Match(frame, Address, 2, strict: false, out _));
    }

    [Fact]
    public void Match_OtherAddress_IsInvalidImmediately()
        => Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match([0xEE, 0x02], Address, 0, strict: true, out _));

    [Fact]
    public void Match_NoStartOfFrame_IsInvalid()
        => Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match([0x01, 0x01], Address, 0, strict: true, out _));

    [Fact]
    public void Match_StartOfFrameInsideBody_IsInvalid()
        => Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match([0xEE, 0x01, 0xF0, 0xEE], Address, 2, strict: true, out _));

    [Fact]
    public void Match_UnexpectedLength_IsInvalid()
    {
        // 数据区 1 字节：既不是报错应答（0 字节）也不是正常应答（2 字节）
        var frame = Ers272Frame.EncodeResponse(Address, Ers272Code.Ok, [0x01]);

        Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match(frame, Address, 2, strict: true, out _));
    }

    [Fact]
    public void Match_NoEndOfFrameWithinLongestFrame_IsInvalid()
    {
        var noise = new byte[Ers272Frame.Overhead + 2];
        noise[0] = 0xEE;
        noise[1] = Address;

        Assert.Equal(FrameStatus.Invalid, Ers272Frame.Match(noise, Address, 2, strict: true, out _));
    }

    [Fact]
    public void TryParseRequest_RoundTrips_AndReportsChecksum()
    {
        var request = Ers272Frame.EncodeRequest(5, 0x41, [0x01, 0x02, 0x03]);

        Assert.True(Ers272Frame.TryParseRequest(request, out var address, out var command, out var info, out var checksumOk));
        Assert.Equal((5, (byte)0x41, true), (address, command, checksumOk));
        Assert.Equal([0x01, 0x02, 0x03], info);

        request[^2] ^= 0x01;
        Assert.True(Ers272Frame.TryParseRequest(request, out _, out _, out _, out checksumOk));
        Assert.False(checksumOk);
    }
}
