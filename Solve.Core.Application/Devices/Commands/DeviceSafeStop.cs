using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;

namespace Solve.Core.Application.Devices.Commands;

/// <summary>
/// 安全停止的范围
/// </summary>
public enum SafeStopScope
{
    /// <summary>
    /// 只停接线指向的这一路通道（负载卸载 / 关闭这一路输出）
    /// </summary>
    Channel = 1,

    /// <summary>
    /// 停接线所在的整台模块（全部通道），会同时作用到接在这台模块其他通道上的位置
    /// </summary>
    Module = 2,
}

/// <summary>
/// 按位置执行安全停止：在 <see cref="Node"/> 的有效接线里找功能为 <see cref="Kind"/> 的那一路（设置了 <see cref="Channel"/> 时按通道找，
/// 否则按 <see cref="Purpose"/> 找），把它的通道（或整台模块）带到安全状态。安全方向的专用操作，见 <see cref="DeviceCommandService.SafeStopAsync"/>。
/// </summary>
/// <param name="Purpose">用途（如 "VBAT"）；未填用途的接线传 null，比较规则与命令相同；设置了 <see cref="Channel"/> 时不参与匹配</param>
public sealed record DeviceSafeStopRequest(TopologyNodeId Node, ChannelKind Kind, string? Purpose, SafeStopScope Scope)
{
    /// <summary>
    /// 目标通道（模块 + 通道组 + CH 号），规则同 <see cref="DeviceCommandRequest.Channel"/>
    /// </summary>
    public ChannelRef? Channel { get; init; }
}

/// <summary>
/// 安全停止已由设备确认（驱动的安全状态交互成功结束）
/// </summary>
/// <param name="Position">选中位置的编码</param>
/// <param name="ModuleText">"总线 · 模块"</param>
/// <param name="TargetText">停止的对象，如"负载 2"或"整台模块"</param>
/// <param name="Elapsed">从下发到确认的耗时</param>
public sealed record DeviceSafeStopResult(string Position, string ModuleText, string TargetText, TimeSpan Elapsed);
