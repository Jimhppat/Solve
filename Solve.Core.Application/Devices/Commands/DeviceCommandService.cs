using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Drivers;
using Solve.Core.Domain.Topology;
using Solve.Core.Domain.Wiring;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices.Commands;

/// <summary>
/// 按拓扑位置下发设备命令：位置 + 通道功能 / 用途 → 有效接线 → （模块，通道）→ 驱动会话的 BeginCommand → 总线事务。
/// </summary>
/// <remarks>
/// <para>两步：<see cref="Prepare"/> 解析并过联锁，得到预览（需要确认时作为确认文案）→ <see cref="ExecuteAsync"/>
/// 检查确认与有效期，按当前状态重新解析、再过一次联锁，目标没变才发出报文。任何一步不满足都拒绝（故障安全方向）。</para>
/// <para>联锁：所有命令都要求选中位置与接线所在位置 <see cref="WiringInspector.CheckReady"/>（位置启用、产品位的接点接完、全部有效接线就绪）。
/// 输出动作与高危命令另要求接线所在位置的子树内没有停用的位置——上级的一路电源会同时作用到维护屏蔽中的产品位。
/// 内核不知道哪条命令是"关闭"方向，所以关闭输出同样受联锁约束；紧急情况走系统急停（全部总线安全跳闸）。</para>
/// <para>执行：与轮询共用总线的通道锁，同一条总线上的报文串行；会话的 BeginCommand 也在锁内调用，保证同一会话的调用串行。
/// 命令失败不自动重试：命令不一定可以重复执行（如脉冲、计数类动作），由操作员核对现场后决定。
/// 驱动抛出异常按 SDK 约定视为通信状态未知，所在总线由总线运行时安全跳闸。</para>
/// <para>仿真：传输模式由组合根决定，仿真模式下总线通道是仿真通道，命令不会到达实机。</para>
/// <para>线程：可在任意线程调用；解析只读内存快照。每条命令的下发、结果与拒绝都写结构化日志（位置编码、模块、通道、结果、耗时）。</para>
/// </remarks>
public sealed class DeviceCommandService
{
    private readonly WiringInspector _inspector;
    private readonly IDeviceBindings _bindings;
    private readonly IBusManagementService _buses;
    private readonly ExchangeExecutor _executor;
    private readonly DeviceCommandOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DeviceCommandService(
        WiringInspector inspector,
        IDeviceBindings bindings,
        IBusManagementService buses,
        ExchangeExecutor executor,
        DeviceCommandOptions options,
        TimeProvider time,
        ILogger<DeviceCommandService> logger)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(buses);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        if (options.Timeout <= TimeSpan.Zero || options.ConfirmationLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "命令超时与确认有效期必须为正。");

        _inspector = inspector;
        _bindings = bindings;
        _buses = buses;
        _executor = executor;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public bool IsHardware => _options.IsHardware;

    /// <summary>
    /// 一路接线上可以下发的命令：作用于这组通道的通道级命令；驱动只声明了一路通道时也包括模块级命令。
    /// 模块未就绪（无驱动实现）时为空。
    /// </summary>
    public IReadOnlyList<DeviceCommandOption> ListCommands(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (!_bindings.TryGet(wire.Channel.Device, out var binding) || binding.Driver is not { } driver)
            return [];

        var single = TotalChannels(driver) == 1;
        return driver.Commands
            .Where(c => Enum.IsDefined(c.Safety)
                        && (string.Equals(c.Group, wire.Channel.Group, StringComparison.Ordinal) || c.Group is null && single))
            .Select(c => new DeviceCommandOption(c.Key, c.Name, c.Safety))
            .ToList();
    }

    /// <summary>
    /// 解析命令并过联锁，得到预览。纯内存操作，不访问硬件；拒绝时写日志并返回原因。
    /// </summary>
    public Result<DeviceCommandPlan> Prepare(DeviceCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = Resolve(request);
        if (plan.IsFailure)
            LogRejected(request, plan.Error);
        return plan;
    }

    /// <summary>
    /// 执行预览中的命令。<paramref name="confirmed"/> 表示操作员已在确认文案上点了执行；
    /// 需要确认而未确认、预览已过期、确认后目标或联锁变了，都拒绝且不发出报文。
    /// </summary>
    /// <remarks>
    /// 有限时完成：总时限 <see cref="DeviceCommandOptions.Timeout"/>。调用方取消时抛出 <see cref="OperationCanceledException"/>
    /// （已发出的报文无法撤回，日志记为结果未知）。
    /// </remarks>
    public async Task<Result<DeviceCommandResult>> ExecuteAsync(DeviceCommandPlan plan, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(plan.Owner, this))
            throw new ArgumentException("预览不是由本服务创建的。", nameof(plan));

        if (plan.RequiresConfirmation && !confirmed)
            return Rejected(plan.Request, DeviceCommandErrors.ConfirmationRequired(plan.CommandName));
        if (_time.GetUtcNow() > plan.ExpiresAt)
            return Rejected(plan.Request, DeviceCommandErrors.Expired(plan.CommandName, _options.ConfirmationLifetime));

        // 确认到执行之间现场可能已变化：按当前状态重新解析、重新过联锁，目标不同即作废
        var current = Resolve(plan.Request);
        if (current.IsFailure)
            return Rejected(plan.Request, current.Error);
        if (!SameTarget(plan, current.Value))
            return Rejected(plan.Request, DeviceCommandErrors.Stale(plan.CommandName));

        if (_buses.GetRuntime(plan.Binding.Device.BusId) is not { IsSuccess: true } runtime)
            return Rejected(plan.Request, DeviceCommandErrors.BusUnavailable(BusErrors.NotRegistered(plan.Binding.Device.BusId)));

        return await RunAsync(plan, runtime.Value, confirmed, cancellationToken).ConfigureAwait(false);
    }

    // ==================== 执行 ====================

    private async Task<Result<DeviceCommandResult>> RunAsync(
        DeviceCommandPlan plan,
        BusRuntime runtime,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        var session = plan.Binding.Session!;
        var command = new DeviceCommand(plan.Command.Key, plan.Request.Arguments) { Channel = plan.Channel };
        var label = $"{plan.Target.Address} 命令 {plan.Command.Key} → {plan.Binding.Device}";

        _logger.LogInformation(
            "设备命令下发 {Command}（{CommandKey}，{Safety}）位置 {Position} 接线 {Wire} 来源 {Source} 模块 {Module} 地址 {Address} 通道 {Channel} 通道号 {ChannelIndex} 影响产品位 {Affected} 模式 {Mode} 已确认 {Confirmed}",
            plan.CommandName, plan.Command.Key, plan.Safety, plan.Target.Address, plan.Wire.KindText, plan.Source.Address,
            plan.ModuleText, plan.Binding.Device.Address, plan.ChannelText, plan.Channel, plan.AffectedProducts,
            plan.IsHardware ? "实机" : "仿真", confirmed);

        var started = _time.GetTimestamp();
        using var timeout = new CancellationTokenSource(_options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        ExchangeOutcome outcome;
        try
        {
            outcome = await _executor.ExecuteAsync(runtime, () => session.BeginCommand(command), label, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Finished(plan, started, Result<DeviceCommandResult>.Failure(DeviceCommandErrors.Timeout(plan.CommandName, _options.Timeout)), LogLevel.Error);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("设备命令被取消 {Command} 位置 {Position} 模块 {Module} 通道 {Channel}：若报文已发出，结果未知，耗时 {ElapsedMs:0} ms",
                plan.CommandName, plan.Target.Address, plan.ModuleText, plan.ChannelText, _time.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (ObjectDisposedException) when (_buses.GetRuntime(runtime.Id) is not { IsSuccess: true } still || !ReferenceEquals(still.Value, runtime))
        {
            // 总线在执行途中被删除或重建（不是驱动抛出的，驱动异常走下面的安全跳闸分支）
            var removed = DeviceCommandErrors.BusUnavailable(BusErrors.NotRegistered(runtime.Id));
            return Finished(plan, started, Result<DeviceCommandResult>.Failure(removed), LogLevel.Warning);
        }
        catch (Exception ex)
        {
            // SDK 约定：驱动抛异常即通信状态未知。事务内的异常已由总线运行时安全跳闸后原样抛出，这里只记录
            _logger.LogCritical(ex, "设备命令 {Command} 位置 {Position}：驱动 {Driver} 抛出异常，总线 {BusId} 已安全跳闸",
                plan.CommandName, plan.Target.Address, plan.Binding.Device.Driver, runtime.Id);
            var fault = DeviceCommandErrors.DriverFault(plan.Binding.Device.Driver.ToString());
            return Finished(plan, started, Result<DeviceCommandResult>.Failure(fault), LogLevel.Critical);
        }

        return outcome switch
        {
            ExchangeOutcome.Succeeded => Finished(plan, started,
                Result<DeviceCommandResult>.Success(new DeviceCommandResult(plan, _time.GetElapsedTime(started))), LogLevel.Information),

            ExchangeOutcome.DeviceFailed { Kind: FailureKind.Configuration } failed => Finished(plan, started,
                Result<DeviceCommandResult>.Failure(DeviceCommandErrors.Configuration(plan.CommandName, failed.Reason)), LogLevel.Error),

            ExchangeOutcome.DeviceFailed { Kind: FailureKind.Device } failed => Finished(plan, started,
                Result<DeviceCommandResult>.Failure(DeviceCommandErrors.DeviceFault(plan.CommandName, failed.Reason)), LogLevel.Error),

            // 暂时性失败（超时、帧无效、设备忙）：不自动重试
            ExchangeOutcome.DeviceFailed failed => Finished(plan, started,
                Result<DeviceCommandResult>.Failure(DeviceCommandErrors.Transient(plan.CommandName, failed.Reason)), LogLevel.Warning),

            ExchangeOutcome.BusUnavailable unavailable => Finished(plan, started,
                Result<DeviceCommandResult>.Failure(DeviceCommandErrors.BusUnavailable(unavailable.Error)), LogLevel.Warning),

            _ => throw new InvalidOperationException($"未处理的交互结果 {outcome.GetType().Name}"),
        };
    }

    private Result<DeviceCommandResult> Finished(DeviceCommandPlan plan, long started, Result<DeviceCommandResult> result, LogLevel level)
    {
        var elapsed = _time.GetElapsedTime(started).TotalMilliseconds;
        if (result.IsSuccess)
        {
            _logger.Log(level, "设备命令完成 {Command} 位置 {Position} 模块 {Module} 通道 {Channel} 结果 {Outcome} 耗时 {ElapsedMs:0} ms",
                plan.CommandName, plan.Target.Address, plan.ModuleText, plan.ChannelText, "成功", elapsed);
        }
        else
        {
            _logger.Log(level, "设备命令失败 {Command} 位置 {Position} 模块 {Module} 通道 {Channel} 结果 {Outcome} 原因 {Reason} 耗时 {ElapsedMs:0} ms",
                plan.CommandName, plan.Target.Address, plan.ModuleText, plan.ChannelText, result.Error.Code, result.Error.Message, elapsed);
        }

        return result;
    }

    private Result<DeviceCommandResult> Rejected(DeviceCommandRequest request, Error error)
    {
        LogRejected(request, error);
        return Result<DeviceCommandResult>.Failure(error);
    }

    private void LogRejected(DeviceCommandRequest request, Error error)
    {
        var position = _inspector.Wiring.Current.Topology is { } topology && topology.TryGetNode(request.Node, out var node)
            ? node.Address.ToString()
            : $"节点 {request.Node}";
        _logger.LogWarning("设备命令拒绝 {CommandKey} 位置 {Position} 功能 {Kind} 用途 {Purpose} 结果 {Outcome} 原因 {Reason}",
            request.CommandKey, position, request.Kind, request.Purpose, error.Code, error.Message);
    }

    /// <summary>
    /// 确认时与执行时解析到的是同一个目标：同一路接线、同一个落点、同一个会话（模块与驱动未被重新绑定）、
    /// 同一条命令声明与通道、相同的受影响范围
    /// </summary>
    private static bool SameTarget(DeviceCommandPlan confirmed, DeviceCommandPlan current)
        => confirmed.Wire.Id == current.Wire.Id
           && confirmed.Source.Id == current.Source.Id
           && ReferenceEquals(confirmed.Binding.Session, current.Binding.Session)
           && confirmed.Command == current.Command
           && confirmed.Channel == current.Channel
           && confirmed.AffectedProducts == current.AffectedProducts
           && confirmed.RequiresConfirmation == current.RequiresConfirmation;

    // ==================== 解析与联锁 ====================

    private Result<DeviceCommandPlan> Resolve(DeviceCommandRequest request)
    {
        if (ValidateRequest(request) is { } invalid)
            return Result<DeviceCommandPlan>.Failure(invalid);

        var map = _inspector.Wiring.Current;
        if (map.Topology is not { } topology || !topology.TryGetNode(request.Node, out var target))
            return Result<DeviceCommandPlan>.Failure(WiringErrors.NodeNotFound());

        // 同一路径上同功能同用途只能有一路（接线规则保证），这里仍按"恰好一路"校验，多于一路即拒绝
        var purposeKey = Wire.NormalizeText(request.Purpose)?.ToUpperInvariant() ?? string.Empty;
        var matches = map.GetEffective(target.Id)
            .Where(e => e.Wire.Kind == request.Kind && e.Wire.PurposeKey == purposeKey)
            .ToList();
        var kindText = purposeKey.Length > 0 ? $"{request.Kind.DisplayName()} {Wire.NormalizeText(request.Purpose)}" : request.Kind.DisplayName();
        if (matches.Count == 0)
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.NotWired(target.Address.ToString(), kindText));
        if (matches.Count > 1)
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.InvalidRequest($"{target.Address} 有 {matches.Count} 路「{kindText}」接线，无法确定命令的目标。"));

        var (wire, source) = (matches[0].Wire, matches[0].Source);

        // 联锁：选中位置与接线所在位置都要就绪
        var ready = _inspector.CheckReady(target.Id);
        if (ready.IsSuccess && source.Id != target.Id)
            ready = _inspector.CheckReady(source.Id);
        if (ready.IsFailure)
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.Interlock(ready.Error));

        // 联锁已要求模块在线；这里是防御性检查（快照之间可能刚被重新绑定）
        if (!_bindings.TryGet(wire.Channel.Device, out var binding)
            || binding is not { Status: DriverBindingStatus.Ready, Session: not null, Driver: { } driver })
        {
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.Interlock(new Error("Wiring.NotReady", "接线的模块未就绪。")));
        }

        var command = driver.Commands.FirstOrDefault(c => string.Equals(c.Key, request.CommandKey, StringComparison.Ordinal));
        if (command is null || !Enum.IsDefined(command.Safety))
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.UnknownCommand(driver.Name, request.CommandKey));

        var group = driver.Channels.FirstOrDefault(g => string.Equals(g.Key, wire.Channel.Group, StringComparison.Ordinal));
        var channelText = group is null ? $"{wire.Channel.Group} {wire.Channel.Index}" : ChannelName(group, wire.Channel.Index);
        int channel;
        if (command.Group is null)
        {
            // 模块级命令作用于整台模块：只有驱动只声明一路通道时，它才等同于这一路接线
            var total = TotalChannels(driver);
            if (total != 1)
                return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.ModuleWide(command.Name, total));
            channel = 0;
        }
        else if (string.Equals(command.Group, wire.Channel.Group, StringComparison.Ordinal))
        {
            channel = wire.Channel.Index;
        }
        else
        {
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.NotForChannel(command.Name, channelText));
        }

        if (command.Safety is CommandSafety.Output or CommandSafety.Hazardous && FindDisabledBelow(topology, source) is { } disabled)
        {
            return Result<DeviceCommandPlan>.Failure(DeviceCommandErrors.SubtreeDisabled(
                command.Name, $"{source.Level.DisplayName()} {source.Address}", $"{disabled.Level.DisplayName()} {disabled.Address}"));
        }

        var affected = source.Level == TopologyLevel.Product ? 1 : topology.GetDescendants(source.Address, TopologyLevel.Product).Count;
        var busName = _buses.GetRuntime(binding.Device.BusId) is { IsSuccess: true } runtime ? runtime.Value.Descriptor.Name : "未知总线";
        var frozen = request with { Arguments = request.Arguments.ToFrozenDictionary(StringComparer.Ordinal) };

        return Result<DeviceCommandPlan>.Success(new DeviceCommandPlan(
            this,
            frozen,
            target,
            source,
            wire,
            binding,
            command,
            channel,
            $"{busName} · {binding.Device.Name}",
            channelText,
            affected,
            _options.IsHardware,
            _time.GetUtcNow() + _options.ConfirmationLifetime));
    }

    private static Error? ValidateRequest(DeviceCommandRequest request)
    {
        if (request.Node.IsEmpty)
            return DeviceCommandErrors.InvalidRequest("没有指定位置。");
        if (!request.Kind.IsDefined())
            return DeviceCommandErrors.InvalidRequest($"通道功能未定义：{(int)request.Kind}。");
        if (!KeyFormat.IsValid(request.CommandKey))
            return DeviceCommandErrors.InvalidRequest($"命令标识「{request.CommandKey}」格式无效。");

        // 记录的 init 属性可被设成 null，不能信任可空注解
        if (request.Arguments is not { } arguments)
            return DeviceCommandErrors.InvalidRequest("命令参数表不能为 null。");
        if (arguments.Count > DeviceCommandOptions.MaxArguments)
            return DeviceCommandErrors.InvalidRequest($"命令参数不能超过 {DeviceCommandOptions.MaxArguments} 个。");

        foreach (var (key, value) in arguments)
        {
            if (!KeyFormat.IsValid(key))
                return DeviceCommandErrors.InvalidRequest($"参数标识「{key}」格式无效。");
            if (!double.IsFinite(value))
                return DeviceCommandErrors.InvalidRequest($"参数 {key} 的值无效（{value}）。");
        }

        return null;
    }

    /// <summary>
    /// 子树内（不含自身）第一个本身停用的位置；停用的位置连同其下级都处于维护屏蔽
    /// </summary>
    private static TopologyNode? FindDisabledBelow(EquipmentTopology topology, TopologyNode source)
    {
        for (var level = source.Level + 1; level <= TopologyLevel.Product; level++)
        {
            foreach (var node in topology.GetDescendants(source.Address, level))
            {
                if (!node.IsEnabled)
                    return node;
            }
        }

        return null;
    }

    private static int TotalChannels(DriverInfo driver) => driver.Channels.Sum(g => g.Count);

    private static string ChannelName(ChannelGroupInfo group, int index) => group.Count == 1 ? group.Name : $"{group.Name} {index}";
}
