using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Wiring;

/// <summary>
/// 接线的错误码。Code 一经发布即视为稳定接口（日志检索、告警映射依赖它）；Message 面向操作员，不带系统 Id。
/// </summary>
public static class WiringErrors
{
    public const string FieldCodePrefix = "Wiring.Field.";
    public const string InvalidWireCode = "Wiring.InvalidWire";
    public const string NodeNotFoundCode = "Wiring.NodeNotFound";
    public const string SystemNodeCode = "Wiring.SystemNode";
    public const string ChannelInUseCode = "Wiring.ChannelInUse";
    public const string PathConflictCode = "Wiring.PathConflict";
    public const string DeviceNotFoundCode = "Wiring.DeviceNotFound";
    public const string ChannelNotDeclaredCode = "Wiring.ChannelNotDeclared";
    public const string KindMismatchCode = "Wiring.KindMismatch";
    public const string NotEnoughChannelsCode = "Wiring.NotEnoughChannels";
    public const string NoTargetsCode = "Wiring.NoTargets";
    public const string WireNotFoundCode = "Wiring.WireNotFound";
    public const string ChannelsInUseByWiresCode = "Wiring.ChannelsInUseByWires";
    public const string StalePlanCode = "Wiring.StalePlan";
    public const string NoTopologyCode = "Wiring.NoTopology";
    public const string LoadFailedCode = "Wiring.LoadFailed";
    public const string StorageFailedCode = "Wiring.Storage.Failed";
    public const string StorageConflictCode = "Wiring.Storage.Conflict";
    public const string StorageNotFoundCode = "Wiring.Storage.NotFound";
    public const string StorageCorruptRowCode = "Wiring.Storage.CorruptRow";

    /// <summary>
    /// 一次报告中最多列出的冲突数：批量接线一处写错可能重复上百次
    /// </summary>
    public const int MaxListed = 10;

    public static Error InvalidField(string field, string message) => new(FieldCodePrefix + field, message);

    public static Error InvalidWire(IEnumerable<Error> errors) => new(InvalidWireCode, string.Join("；", errors.Select(e => e.Message)));

    public static Error NodeNotFound() => new(NodeNotFoundCode, "位置已不存在，可能已被删除。");

    public static Error SystemNode() => new(SystemNodeCode, "系统根不能接线，请选择柜体、层位、槽位、治具位或产品位。");

    public static Error ChannelInUse(IReadOnlyList<string> details)
        => new(ChannelInUseCode, $"通道已接到其他位置：{List(details)}。一路通道只能接到一个位置；几个位置共用时请接在它们的上级。");

    public static Error PathConflict(IReadOnlyList<string> details)
        => new(PathConflictCode, $"同一位置路径上已有相同功能、相同用途的接线：{List(details)}。如确需多路，请为每一路填写不同的用途。");

    public static Error DeviceNotFound() => new(DeviceNotFoundCode, "模块已不存在，可能已被删除。");

    public static Error ChannelNotDeclared(string detail) => new(ChannelNotDeclaredCode, detail);

    public static Error KindMismatch(string detail) => new(KindMismatchCode, detail);

    public static Error NotEnoughChannels(int needed, int available)
        => new(NotEnoughChannelsCode, $"通道不够：需要 {needed} 路，从起始通道起只有 {available} 路可用。");

    public static Error NoTargets() => new(NoTargetsCode, "所选范围内没有要接线的位置。");

    public static Error WireNotFound() => new(WireNotFoundCode, "接线已不存在，可能已被删除。");

    public static Error ChannelsInUseByWires(string detail) => new(ChannelsInUseByWiresCode, detail);

    public static Error StalePlan() => new(StalePlanCode, "接线或拓扑已被修改，请重新预览后再保存。");

    public static Error NoTopology() => new(NoTopologyCode, "设备拓扑尚未建档或读取失败，不能接线。");

    public static Error LoadFailed(Error cause) => new(LoadFailedCode, $"接线读取失败，已禁止修改：{cause.Message}");

    public static Error StorageFailed(string detail) => new(StorageFailedCode, $"接线存储失败：{detail}");

    public static Error StorageConflict() => new(StorageConflictCode, "部分通道已被其他接线占用，或位置、模块已被删除，请刷新后重试。");

    public static Error StorageNotFound() => new(StorageNotFoundCode, "接线已不存在，可能已被删除。");

    public static Error StorageCorruptRow(string field, string detail)
        => new(StorageCorruptRowCode, $"接线行字段 {field} 无法解析：{detail}");

    private static string List(IReadOnlyList<string> details)
        => details.Count > MaxListed
            ? $"{string.Join("；", details.Take(MaxListed))} 等 {details.Count} 处"
            : string.Join("；", details);
}
