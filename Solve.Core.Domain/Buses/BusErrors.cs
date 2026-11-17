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
}
