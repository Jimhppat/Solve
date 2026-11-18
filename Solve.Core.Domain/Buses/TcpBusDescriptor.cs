using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// TCP 客户端总线描述（上位机主动连接设备或串口服务器）。
/// </summary>
/// <param name="Host">IPv4 / IPv6 地址或主机名</param>
/// <param name="Port">TCP 端口，1–65535（Modbus TCP 默认 502）</param>
public sealed record TcpBusDescriptor(BusId Id, string Name, string Host, int Port) : BusDescriptor(Id, Name)
{
    protected override void CollectLinkErrors(List<Error> errors)
    {
        // 只校验格式，不在领域层做 DNS 解析（解析属于 I/O，由 Transport 在连接超时内完成）
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            errors.Add(BusErrors.InvalidField("Host", $"主机地址无效：'{Host}'。"));

        if (Port is < 1 or > 65535)
            errors.Add(BusErrors.InvalidField("Port", $"端口必须在 [1, 65535] 之间，当前为 {Port}。"));
    }

    public override bool RequiresTransportRebuild(BusDescriptor other)
    {
        if (other is not TcpBusDescriptor tcp)
            return true;

        return !string.Equals(Host.Trim(), tcp.Host.Trim(), StringComparison.OrdinalIgnoreCase)
            || Port != tcp.Port;
    }
}
