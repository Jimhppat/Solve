using Solve.Core.Domain.Common;

namespace Solve.Core.Application.Devices.Commands;

/// <summary>
/// 按位置下发设备命令的错误。拒绝类（联锁、确认、过期）都发生在报文发出之前；执行类说明报文已经或可能已经发出。
/// </summary>
public static class DeviceCommandErrors
{
    // ---------- 拒绝：报文未发出 ----------

    public const string InvalidRequestCode = "Command.InvalidRequest";
    public const string NotWiredCode = "Command.NotWired";
    public const string UnknownCommandCode = "Command.Unknown";
    public const string NotForChannelCode = "Command.NotForChannel";
    public const string ModuleWideCode = "Command.ModuleWide";
    public const string InterlockCode = "Command.Interlock";
    public const string SubtreeDisabledCode = "Command.SubtreeDisabled";
    public const string ConfirmationRequiredCode = "Command.ConfirmationRequired";
    public const string ExpiredCode = "Command.Expired";
    public const string StaleCode = "Command.Stale";

    // ---------- 执行：报文已经或可能已经发出 ----------

    public const string TransientCode = "Command.Transient";
    public const string ConfigurationCode = "Command.Configuration";
    public const string DeviceFaultCode = "Command.DeviceFault";
    public const string BusUnavailableCode = "Command.BusUnavailable";
    public const string TimeoutCode = "Command.Timeout";
    public const string DriverFaultCode = "Command.DriverFault";

    public static Error InvalidRequest(string message) => new(InvalidRequestCode, message);

    public static Error NotWired(string position, string kind)
        => new(NotWiredCode, $"{position} 没有「{kind}」接线。");

    public static Error UnknownCommand(string driver, string key)
        => new(UnknownCommandCode, $"{driver}没有命令 {key}。");

    public static Error NotForChannel(string command, string channel)
        => new(NotForChannelCode, $"「{command}」不是作用于{channel}的命令。");

    public static Error ModuleWide(string command, int channels)
        => new(ModuleWideCode, $"「{command}」作用于整台模块（共 {channels} 路通道），会影响接在其他位置上的通道，不能按位置下发。");

    public static Error Interlock(Error reason) => new(InterlockCode, $"联锁未满足：{reason.Message}");

    public static Error SubtreeDisabled(string command, string source, string disabled)
        => new(SubtreeDisabledCode, $"联锁未满足：{disabled} 已停用（维护屏蔽），「{command}」经 {source} 的接线会同时作用到它。");

    public static Error ConfirmationRequired(string command)
        => new(ConfirmationRequiredCode, $"「{command}」需要操作员确认后才能执行。");

    public static Error Expired(string command, TimeSpan lifetime)
        => new(ExpiredCode, $"「{command}」的确认已超过 {lifetime.TotalSeconds:0} s，现场状态可能已变化，请重新下发。");

    public static Error Stale(string command)
        => new(StaleCode, $"确认之后接线、模块或驱动已变化，「{command}」未执行，请重新下发。");

    /// <summary>
    /// 等不到通道锁、总线未连接时报文未发出；发送后链路故障时设备可能已收到，所以统一说"未完成"
    /// </summary>
    public static Error BusUnavailable(Error bus) => new(BusUnavailableCode, $"总线不可用，命令未完成：{bus.Message}");

    public static Error Transient(string command, string reason)
        => new(TransientCode, $"「{command}」失败：{reason} 命令不自动重试，核对现场后可再次下发。");

    public static Error Configuration(string command, string reason)
        => new(ConfigurationCode, $"「{command}」被驱动判为配置错误：{reason}");

    public static Error DeviceFault(string command, string reason)
        => new(DeviceFaultCode, $"设备报告故障，「{command}」未完成：{reason}");

    public static Error Timeout(string command, TimeSpan timeout)
        => new(TimeoutCode, $"「{command}」在 {timeout.TotalSeconds:0} s 内未完成，执行结果未知，请核对设备状态。");

    public static Error DriverFault(string driver)
        => new(DriverFaultCode, $"驱动 {driver} 在执行命令时抛出异常，所在总线已安全跳闸，命令结果未知；复位总线后才能恢复通信。");
}
