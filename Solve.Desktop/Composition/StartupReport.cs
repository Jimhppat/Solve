using System;
using System.Collections.Generic;
using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Composition;

/// <summary>
/// 一条读取成功但注册失败的总线配置（如与其他总线争用同一串口）
/// </summary>
public sealed record BusRegistrationFailure(BusId BusId, string Name, Error Error);

/// <summary>
/// 启动结果，供以后的 UI 展示"本次以什么模式运行、哪些配置有问题"。
/// </summary>
/// <param name="Mode">传输模式决定；配置库都打不开时为 null</param>
/// <param name="Fatal">导致总线系统未能完整启动的错误；为 null 表示启动成功</param>
public sealed record StartupReport(
    TransportModeDecision? Mode,
    int RegisteredCount,
    IReadOnlyList<BusConfigurationIssue> ConfigurationIssues,
    IReadOnlyList<BusRegistrationFailure> RegistrationFailures,
    Error? Fatal)
{
    public bool Succeeded => Fatal is null;

    public static StartupReport Failed(TransportModeDecision? mode, Error fatal)
        => new(mode, 0, Array.Empty<BusConfigurationIssue>(), Array.Empty<BusRegistrationFailure>(), fatal);
}
