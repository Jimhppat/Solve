using System;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Composition;

public enum TransportMode
{
    /// <summary>
    /// 仿真通道，不触碰任何设备（默认）
    /// </summary>
    Simulation = 0,

    /// <summary>
    /// 真实串口 / TCP
    /// </summary>
    Hardware = 1,
}

public sealed record TransportModeDecision(TransportMode Mode, string Reason);

/// <summary>
/// 实机模式双重确认：命令行 --hardware 与配置库 bus.hardware_enabled = "true" 必须同时满足，
/// 其余任何情况（含读取失败、值非法）一律仿真。
/// </summary>
public static class TransportModePolicy
{
    public const string HardwareEnabledKey = "bus.hardware_enabled";
    public const string EnabledValue = "true";
    public const string DisabledValue = "false";

    public static TransportModeDecision Decide(bool hardwareRequested, Result<string?> storedSetting)
    {
        if (!hardwareRequested)
            return Simulation($"未指定 {DesktopOptions.HardwareArgument}");

        if (storedSetting.IsFailure)
            return Simulation($"已指定 {DesktopOptions.HardwareArgument}，但读取 {HardwareEnabledKey} 失败（{storedSetting.Error}），按仿真运行");

        return storedSetting.Value switch
        {
            // 严格匹配小写 "true"：大小写变体、"1"、"yes" 等都不视为授权
            EnabledValue => new TransportModeDecision(TransportMode.Hardware,
                $"{DesktopOptions.HardwareArgument} 与 {HardwareEnabledKey}=true 同时满足"),
            null => Simulation($"已指定 {DesktopOptions.HardwareArgument}，但配置库未设置 {HardwareEnabledKey}"),
            DisabledValue => Simulation($"已指定 {DesktopOptions.HardwareArgument}，但 {HardwareEnabledKey}=false"),
            var other => Simulation($"已指定 {DesktopOptions.HardwareArgument}，但 {HardwareEnabledKey} 的值 '{other}' 非法（只接受 true / false）"),
        };
    }

    private static TransportModeDecision Simulation(string reason) => new(TransportMode.Simulation, reason);
}
