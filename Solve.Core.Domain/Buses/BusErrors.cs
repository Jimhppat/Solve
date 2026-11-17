using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// 总线错误码。Code 一经发布即视为稳定接口（日志检索、告警映射、UI 字段高亮依赖它）。
/// </summary>
public static class BusErrors
{
    public const string InvalidTransitionCode = "Bus.InvalidTransition";

    /// <summary>
    /// 配置字段错误的 Code 前缀，完整 Code 形如 Bus.Descriptor.BaudRate
    /// </summary>
    public const string DescriptorCodePrefix = "Bus.Descriptor.";

    public static Error InvalidTransition(BusState from, BusTrigger trigger)
        => new(InvalidTransitionCode, $"总线状态 {from} 不接受触发 {trigger}。");

    public static Error InvalidField(string field, string message)
        => new(DescriptorCodePrefix + field, message);

    // ---------- 物理通道（Transport）----------

    public const string NotOpenCode = "Bus.Transport.NotOpen";
    public const string AlreadyOpenCode = "Bus.Transport.AlreadyOpen";
    public const string OpenFailedCode = "Bus.Transport.OpenFailed";
    public const string IoFailedCode = "Bus.Transport.IoFailed";
    public const string ConnectionClosedCode = "Bus.Transport.ConnectionClosed";
    public const string UnsupportedDescriptorCode = "Bus.Transport.UnsupportedDescriptor";
    public const string InvalidDescriptorCode = "Bus.Transport.InvalidDescriptor";

    public static Error NotOpen() => new(NotOpenCode, "物理通道未打开。");

    public static Error AlreadyOpen() => new(AlreadyOpenCode, "物理通道已打开。");

    public static Error OpenFailed(string detail) => new(OpenFailedCode, $"打开物理通道失败：{detail}");

    public static Error IoFailed(string detail) => new(IoFailedCode, $"物理通道读写失败：{detail}");

    public static Error ConnectionClosed() => new(ConnectionClosedCode, "物理通道已关闭或被对端断开。");

    public static Error UnsupportedDescriptor(Type descriptorType)
        => new(UnsupportedDescriptorCode, $"不支持的总线类型：{descriptorType.Name}。");

    public static Error InvalidDescriptor(IReadOnlyList<Error> errors)
        => new(InvalidDescriptorCode, "总线配置无效：" + string.Join("；", errors.Select(e => e.ToString())));
}
