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

    // ---------- 运行时（BusRuntime）----------

    public const string TimeoutCode = "Bus.Timeout";
    public const string ChannelLockedCode = "Bus.ChannelLocked";
    public const string ChannelBusyCode = "Bus.ChannelBusy";
    public const string InterlockActiveCode = "Bus.InterlockActive";
    public const string SafetyTrippedCode = "Bus.SafetyTripped";
    public const string HeartbeatLostCode = "Bus.HeartbeatLost";
    public const string RetryExhaustedCode = "Bus.RetryExhausted";
    public const string UnhandledExceptionCode = "Bus.UnhandledException";

    public static Error Timeout(string operation, TimeSpan timeout)
        => new(TimeoutCode, $"{operation}超时（{timeout.TotalMilliseconds:0} ms）。");

    public static Error ChannelLocked(BusState state)
        => new(ChannelLockedCode, $"总线处于 {state}，通道已锁定，不允许收发。");

    public static Error ChannelBusy(TimeSpan waited)
        => new(ChannelBusyCode, $"等待通道锁超过 {waited.TotalMilliseconds:0} ms，通道被其他事务占用。");

    public static Error InterlockActive(string detail)
        => new(InterlockActiveCode, $"安全联锁未解除，禁止复位：{detail}");

    public static Error SafetyTripped(string reason)
        => new(SafetyTrippedCode, $"安全跳闸：{reason}");

    public static Error HeartbeatLost()
        => new(HeartbeatLostCode, "心跳丢失。");

    public static Error RetryExhausted(int attempts)
        => new(RetryExhaustedCode, $"自动重连 {attempts} 次均失败，进入故障锁存。");

    public static Error UnhandledException(Exception exception)
        => new(UnhandledExceptionCode, $"未处理异常 {exception.GetType().Name}：{exception.Message}");

    // ---------- 注册表（BusManager）----------

    public const string AlreadyRegisteredCode = "Bus.AlreadyRegistered";
    public const string NotRegisteredCode = "Bus.NotRegistered";
    public const string ResourceConflictCode = "Bus.ResourceConflict";

    public static Error AlreadyRegistered(BusId id) => new(AlreadyRegisteredCode, $"总线 {id} 已注册。");

    public static Error NotRegistered(BusId id) => new(NotRegisteredCode, $"总线 {id} 未注册。");

    public static Error ResourceConflict(string resource, BusId owner)
        => new(ResourceConflictCode, $"物理资源 {resource} 已被总线 {owner} 占用。");

    // ---------- 配置存储 ----------

    public const string StorageFailedCode = "Bus.Storage.Failed";
    public const string StorageNotFoundCode = "Bus.Storage.NotFound";
    public const string StorageConflictCode = "Bus.Storage.Conflict";
    public const string StorageCorruptRowCode = "Bus.Storage.CorruptRow";

    public static Error StorageFailed(string detail) => new(StorageFailedCode, $"配置存储失败：{detail}");

    public static Error StorageNotFound(BusId id) => new(StorageNotFoundCode, $"配置中不存在总线 {id}。");

    public static Error StorageConflict(string detail) => new(StorageConflictCode, $"配置冲突：{detail}");

    public static Error StorageCorruptRow(string field, string detail)
        => new(StorageCorruptRowCode, $"配置行字段 {field} 无法解析：{detail}");
}
