using System.Collections.Frozen;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices.Commands;

/// <summary>
/// 按位置下发命令的参数
/// </summary>
public sealed record DeviceCommandOptions
{
    public static DeviceCommandOptions Default { get; } = new();

    /// <summary>
    /// 一条命令最多的参数个数，拦截误传的超大参数表
    /// </summary>
    public const int MaxArguments = 16;

    /// <summary>
    /// 传输为实机（真实串口 / TCP）。实机下输出动作（<see cref="CommandSafety.Output"/>）也要操作员确认；
    /// 仿真下只有高危命令需要确认。默认 false（仿真），与组合根的默认传输模式一致。
    /// </summary>
    public bool IsHardware { get; init; }

    /// <summary>
    /// 一条命令从排队等通道锁到交互结束的总时限（默认 20 s）：覆盖通道锁等待 5 s + 事务 10 s 并留余量，
    /// 是总线运行时自身限时之外的兜底，保证调用方一定在有限时间内拿到结果
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 预览（确认）的有效期（默认 30 s）：操作员看到的确认文案超过这个时间未执行即作废，
    /// 避免离开工位回来后按旧的现场状态误点执行
    /// </summary>
    public TimeSpan ConfirmationLifetime { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 按位置下发一条命令：在 <see cref="Node"/> 的有效接线里找功能为 <see cref="Kind"/>、用途为 <see cref="Purpose"/> 的那一路，
/// 向它的模块通道执行 <see cref="CommandKey"/>
/// </summary>
/// <param name="Purpose">用途（如 "VBAT"）；未填用途的接线传 null。比较时忽略大小写与首尾空白，与接线规则一致</param>
public sealed record DeviceCommandRequest(TopologyNodeId Node, ChannelKind Kind, string? Purpose, string CommandKey)
{
    private static readonly IReadOnlyDictionary<string, double> NoArguments = FrozenDictionary<string, double>.Empty;

    /// <summary>
    /// 命令参数（工程值），按驱动的约定；没有参数为空表
    /// </summary>
    public IReadOnlyDictionary<string, double> Arguments { get; init; } = NoArguments;
}

/// <summary>
/// 一路接线上可以下发的命令
/// </summary>
public sealed record DeviceCommandOption(string Key, string Name, CommandSafety Safety);

public static class CommandSafeties
{
    public static string DisplayName(this CommandSafety safety) => safety switch
    {
        CommandSafety.Parameter => "写参数",
        CommandSafety.Output => "输出动作",
        CommandSafety.Hazardous => "高危",
        _ => $"未定义等级({(int)safety})",
    };
}

/// <summary>
/// 一条已解析、已过联锁的命令预览：执行前展示给操作员（需要确认时作为确认文案），执行时再校验一次。
/// 只能由 <see cref="DeviceCommandService.Prepare"/> 创建；不可变，可跨线程传递。
/// </summary>
/// <remarks>
/// 界面文本只含层级、位置编码、模块名与通道名，不含节点、接线与模块的系统 Id。
/// </remarks>
public sealed class DeviceCommandPlan
{
    internal DeviceCommandPlan(
        DeviceCommandService owner,
        DeviceCommandRequest request,
        TopologyNode target,
        TopologyNode source,
        Wire wire,
        DriverBinding binding,
        CommandInfo command,
        int channel,
        string moduleText,
        string channelText,
        int affectedProducts,
        IReadOnlyList<TopologyNode> sharedWith,
        bool isHardware,
        DateTimeOffset expiresAt)
    {
        SharedWith = sharedWith;
        Owner = owner;
        Request = request;
        Target = target;
        Source = source;
        Wire = wire;
        Binding = binding;
        Command = command;
        Channel = channel;
        ModuleText = moduleText;
        ChannelText = channelText;
        AffectedProducts = affectedProducts;
        IsHardware = isHardware;
        ExpiresAt = expiresAt;
    }

    internal DeviceCommandService Owner { get; }

    internal DriverBinding Binding { get; }

    internal CommandInfo Command { get; }

    /// <summary>
    /// 发给驱动的通道号：通道级命令为接线的通道号，模块级命令为 0
    /// </summary>
    internal int Channel { get; }

    /// <summary>
    /// 请求（参数已冻结，确认之后不会再被修改）
    /// </summary>
    public DeviceCommandRequest Request { get; }

    /// <summary>
    /// 操作员选中的位置
    /// </summary>
    public TopologyNode Target { get; }

    /// <summary>
    /// 接线实际所在的位置（可能是 <see cref="Target"/> 的上级）：命令作用于它的整棵子树
    /// </summary>
    public TopologyNode Source { get; }

    public Wire Wire { get; }

    public string CommandName => Command.Name;

    public CommandSafety Safety => Command.Safety;

    /// <summary>
    /// "总线 · 模块"
    /// </summary>
    public string ModuleText { get; }

    /// <summary>
    /// 通道名，如"输出"、"继电器 3"
    /// </summary>
    public string ChannelText { get; }

    /// <summary>
    /// 受影响的产品位数：接线在产品位上为 1，在上级上为其子树内的全部产品位（含停用的）
    /// </summary>
    public int AffectedProducts { get; }

    /// <summary>
    /// 同一路通道还接在的其他位置（一路通道可以接到多个位置）：命令同样作用到它们
    /// </summary>
    public IReadOnlyList<TopologyNode> SharedWith { get; }

    public bool IsHardware { get; }

    /// <summary>
    /// 执行前需要操作员确认：高危命令每次都要；实机下的输出动作也要
    /// </summary>
    public bool RequiresConfirmation => Command.Safety == CommandSafety.Hazardous
                                        || Command.Safety == CommandSafety.Output && IsHardware;

    /// <summary>
    /// 预览作废的时刻，之后执行一律拒绝
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// 作用范围，如"产品位 C01-L01-S01-F01-P01"、"层位 C01-L02 下全部 8 个产品位"
    /// </summary>
    public string ScopeText => Source.Level == TopologyLevel.Product
        ? $"{Source.Level.DisplayName()} {Source.Address}"
        : $"{Source.Level.DisplayName()} {Source.Address} 下全部 {AffectedProducts} 个产品位";

    /// <summary>
    /// 给操作员看的完整说明（确认文案），多行
    /// </summary>
    public string Describe()
    {
        const int listed = 3;
        var lines = new List<string>(7)
        {
            $"「{CommandName}」（{Safety.DisplayName()}）",
            $"位置：{Target.Level.DisplayName()} {Target.Address}",
            Source.Id == Target.Id
                ? $"接线：{Wire.KindText}"
                : $"接线：{Wire.KindText}，来自{Source.Level.DisplayName()} {Source.Address}",
            $"通道：{ModuleText}  {ChannelText}",
            $"影响：{ScopeText}",
        };
        if (SharedWith.Count > 0)
        {
            lines.Add($"共用：这路通道还接在 {string.Join("、", SharedWith.Take(listed).Select(n => $"{n.Level.DisplayName()} {n.Address}"))}" +
                      $"{(SharedWith.Count > listed ? $" 等 {SharedWith.Count} 个位置" : string.Empty)}，会同时动作");
        }

        lines.Add($"模式：{(IsHardware ? "实机" : "仿真")}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 日志用的一行摘要
    /// </summary>
    public override string ToString()
        => $"「{CommandName}」({Command.Key}, {Safety}) {Target.Address} {Wire.KindText} → {ModuleText} {ChannelText}";
}

/// <summary>
/// 命令执行成功的结果
/// </summary>
/// <param name="Elapsed">从开始排队等通道锁到交互结束的耗时</param>
public sealed record DeviceCommandResult(DeviceCommandPlan Plan, TimeSpan Elapsed);
