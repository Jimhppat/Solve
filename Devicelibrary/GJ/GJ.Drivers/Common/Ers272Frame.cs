using Solve.Device.Sdk;

namespace GJ.Drivers.Common;

/// <summary>
/// ERS272 应答码（应答帧第 3 字节，占请求中 CID 的位置）
/// </summary>
internal enum Ers272Code : byte
{
    Ok = 0xF0,
    ChecksumError = 0xF1,
    LengthError = 0xF2,
    InvalidCommand = 0xF3,
    InvalidData = 0xF4,
}

/// <summary>
/// ERS272 帧：GJ 回馈式电子负载系列共用的二进制主从协议（纯计算，不做 I/O）。
/// </summary>
/// <remarks>
/// <para>依据：旧上位机公共库 GJ.DEV/Load/FLoad.cs 与 GJ.DEV/LED/CDA_750_4.cs 中的 CalDataFromERS272Cmd、ERS_CheckSum、checkErrorCode
/// （没有厂家手册；以下凡未经实机验证的推断都在注释里标明）。</para>
/// <code>
/// 请求：SOI(EE) ADR CID LEN INFO[n] CHK_H CHK_L EOI(EF)
/// 应答：SOI(EE) ADR RTN LEN INFO[n] CHK_H CHK_L EOI(EF)      RTN：F0 正常，F1–F4 见 <see cref="Ers272Code"/>
/// LEN = 3 + n；整帧 = n + 7 字节
/// CHK = (ADR + CID/RTN + LEN + INFO 各字节) mod 0x10000，高字节在前
/// </code>
/// <para>帧头帧尾的唯一性靠"替代"而不是转义：INFO 与校验和中等于 EE / EF 的字节一律改为 ED，接收方不还原。
/// 所以帧内除首尾外不会出现 EE / EF——遇到第一个 EF 即是帧尾，这也是本类成帧的依据。
/// 代价是数据有损：原始值低字节为 EE / EF 的设定值实际下发为 ED（少 1–2 个最小单位），读数同理。</para>
/// <para>ADR、CID、LEN 不做替代：地址 0–127、CID（01–41）与应答码（F0–F4）不会是 EE / EF；
/// LEN = 3 + n，本驱动把数据区限定在 <see cref="MaxInfoLength"/> 字节以内，LEN 也不会取到 EE / EF。</para>
/// </remarks>
internal static class Ers272Frame
{
    public const byte StartOfFrame = 0xEE;
    public const byte EndOfFrame = 0xEF;
    public const byte Substitute = 0xED;

    /// <summary>
    /// 帧的固定开销：SOI + ADR + CID + LEN + CHK(2) + EOI
    /// </summary>
    public const int Overhead = 7;

    /// <summary>
    /// 本驱动用到的最长数据区（写设定 48 字节、读测量值 65 字节），留余量；保证 LEN 不会取到 EE / EF
    /// </summary>
    public const int MaxInfoLength = 128;

    /// <summary>
    /// 合法的设备地址上限：旧驱动 SetAddr 限定 1–127（0 为广播，读地址时使用）
    /// </summary>
    public const int MaxAddress = 127;

    public static byte[] EncodeRequest(int address, byte command, ReadOnlySpan<byte> info) => Build(address, command, info);

    /// <summary>
    /// 编码一帧应答（仿真设备用，按设备同样的替代与校验规则）
    /// </summary>
    public static byte[] EncodeResponse(int address, Ers272Code code, ReadOnlySpan<byte> info) => Build(address, (byte)code, info);

    /// <summary>
    /// 校验和的两个字节（已做 EE / EF → ED 替代）。<paramref name="body"/> 为 ADR 起到 INFO 结束、已替代的字节。
    /// </summary>
    public static (byte High, byte Low) Checksum(ReadOnlySpan<byte> body)
    {
        // body 不超过 3 + MaxInfoLength 字节，int 不会溢出
        var sum = 0;
        foreach (var b in body)
            sum += b;

        return (Replace((byte)(sum >> 8)), Replace((byte)sum));
    }

    /// <summary>
    /// 判断收到的字节是否已构成一帧应答。
    /// </summary>
    /// <param name="address">请求的设备地址：应答的 ADR 必须相同</param>
    /// <param name="infoLength">本次请求成功应答的数据区长度；设备报错（F1–F4）时应答不带数据，两种长度都接受</param>
    /// <param name="strict">核对 LEN 与校验和。旧驱动 FLoad 注释掉了这两项检查（原因未知），所以做成模块参数，实机确认后再定</param>
    public static FrameStatus Match(ReadOnlySpan<byte> received, int address, int infoLength, bool strict, out int frameLength)
    {
        frameLength = 0;
        if (received.IsEmpty)
            return FrameStatus.NeedMoreData;
        if (received[0] != StartOfFrame)
            return FrameStatus.Invalid;
        if (received.Length >= 2 && received[1] != address)
            return FrameStatus.Invalid;

        var longest = Overhead + infoLength;
        var end = -1;
        for (var i = 1; i < received.Length; i++)
        {
            if (received[i] == StartOfFrame)
                return FrameStatus.Invalid;
            if (received[i] == EndOfFrame)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
            return received.Length >= longest ? FrameStatus.Invalid : FrameStatus.NeedMoreData;

        var total = end + 1;
        if (total != Overhead && total != longest)
            return FrameStatus.Invalid;

        if (strict)
        {
            if (received[3] != total - 4)
                return FrameStatus.Invalid;

            var (high, low) = Checksum(received[1..(total - 3)]);
            if (received[total - 3] != high || received[total - 2] != low)
                return FrameStatus.Invalid;
        }

        frameLength = total;
        return FrameStatus.Complete;
    }

    /// <summary>
    /// 应答码（frame 已通过 <see cref="Match"/>）
    /// </summary>
    public static Ers272Code CodeOf(ReadOnlySpan<byte> frame) => (Ers272Code)frame[2];

    /// <summary>
    /// 数据区（frame 已通过 <see cref="Match"/>）
    /// </summary>
    public static ReadOnlySpan<byte> InfoOf(ReadOnlySpan<byte> frame) => frame[4..^3];

    /// <summary>
    /// 设备报错的应答码转为失败：校验和 / 长度错误多半是线路干扰，按暂时性错误重试；
    /// CID 无效（设备不支持该命令）、数据无效（设定值被设备拒绝）重试也不会好，按配置错误处理
    /// </summary>
    public static ExchangeStep.Failed DescribeError(Ers272Code code, string action) => code switch
    {
        Ers272Code.ChecksumError => new($"{action}：设备报告校验和错误（应答码 F1）。", FailureKind.Transient),
        Ers272Code.LengthError => new($"{action}：设备报告长度错误（应答码 F2）。", FailureKind.Transient),
        Ers272Code.InvalidCommand => new($"{action}：设备不支持该命令（应答码 F3，CID 无效）。", FailureKind.Configuration),
        Ers272Code.InvalidData => new($"{action}：设备拒绝了下发的数据（应答码 F4，无效数据）。", FailureKind.Configuration),
        _ => new($"{action}：设备返回未知应答码 0x{(byte)code:X2}。", FailureKind.Transient),
    };

    /// <summary>
    /// 解析一帧完整请求（仿真设备用）。结构不对时返回 false；结构对但校验和不对时返回 true 且 <paramref name="checksumOk"/> 为 false
    /// </summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> request, out int address, out byte command, out byte[] info, out bool checksumOk)
    {
        (address, command, info, checksumOk) = (0, 0, [], false);
        if (request.Length < Overhead || request[0] != StartOfFrame || request[^1] != EndOfFrame)
            return false;

        var body = request[1..^1];
        if (body.Contains(StartOfFrame) || body.Contains(EndOfFrame) || request[3] != request.Length - 4)
            return false;

        address = request[1];
        command = request[2];
        info = request[4..^3].ToArray();
        var (high, low) = Checksum(request[1..^3]);
        checksumOk = request[^3] == high && request[^2] == low;
        return true;
    }

    private static byte[] Build(int address, byte second, ReadOnlySpan<byte> info)
    {
        // 地址已由内核按驱动声明的范围校验、数据区由驱动代码组装：越界属于编程错误，直接抛出
        ArgumentOutOfRangeException.ThrowIfNegative(address);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address, MaxAddress);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(info.Length, MaxInfoLength, nameof(info));

        var frame = new byte[Overhead + info.Length];
        frame[0] = StartOfFrame;
        frame[1] = (byte)address;
        frame[2] = second;
        frame[3] = (byte)(3 + info.Length);
        for (var i = 0; i < info.Length; i++)
            frame[4 + i] = Replace(info[i]);

        (frame[^3], frame[^2]) = Checksum(frame.AsSpan(1, 3 + info.Length));
        frame[^1] = EndOfFrame;
        return frame;
    }

    private static byte Replace(byte value) => value is StartOfFrame or EndOfFrame ? Substitute : value;
}
