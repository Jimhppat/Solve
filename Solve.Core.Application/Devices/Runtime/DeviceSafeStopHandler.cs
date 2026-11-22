using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices.Runtime;

/// <summary>
/// 总线安全跳闸时，按地址顺序对总线上每个实现了 <see cref="ISafeStateProvider"/> 的模块下发安全状态（负载卸载、电源关输出）。
/// </summary>
/// <remarks>
/// <para>由总线运行时在锁存 SafeStop 之后、关闭物理通道之前调用，调用方持有通道锁，所以与轮询、命令天然串行，
/// 也满足 SDK"同一会话的调用串行"的约定。</para>
/// <para>失败处理：单个模块失败（无应答、设备报错、驱动抛异常）记严重日志后继续下一个模块——一台设备不回话
/// 不能连累同一总线上的其他设备；总时限到了就放弃剩下的模块，并逐一记下没有下发的模块，便于现场人工处置。</para>
/// <para>没有实现该能力的就绪模块记警告：跳闸后它们保持最后的设定。</para>
/// </remarks>
public sealed class DeviceSafeStopHandler : IBusSafeStopHandler
{
    private readonly IDeviceBindings _bindings;
    private readonly ExchangeExecutor _executor;
    private readonly ILogger _logger;

    public DeviceSafeStopHandler(IDeviceBindings bindings, ExchangeExecutor executor, ILogger<DeviceSafeStopHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);
        _bindings = bindings;
        _executor = executor;
        _logger = logger;
    }

    public async Task RunAsync(BusId busId, BusDescriptor descriptor, IBusChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(channel);

        var targets = new List<(DriverBinding Binding, ISafeStateProvider Provider)>();
        foreach (var binding in _bindings.Get(busId))
        {
            if (binding is not { Status: DriverBindingStatus.Ready, Session: { } session })
                continue;

            if (session is ISafeStateProvider provider)
                targets.Add((binding, provider));
            else
                _logger.LogWarning("安全跳闸：{Device} 的驱动 {Driver} 没有提供安全状态，保持最后的设定", binding.Device, binding.Device.Driver);
        }

        for (var i = 0; i < targets.Count; i++)
        {
            var (binding, provider) = targets[i];
            try
            {
                await StopOneAsync(channel, descriptor, binding, provider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var (skipped, _) in targets.Skip(i))
                    _logger.LogCritical("安全跳闸：时限已到，{Device} 未下发安全状态，可能仍在输出或带载，请现场确认", skipped.Device);
                throw;
            }
        }
    }

    private async Task StopOneAsync(
        IBusChannel channel,
        BusDescriptor descriptor,
        DriverBinding binding,
        ISafeStateProvider provider,
        CancellationToken cancellationToken)
    {
        var label = $"{binding.Device} 安全状态";
        ExchangeOutcome outcome;
        try
        {
            outcome = await _executor.ExecuteOnChannelAsync(channel, descriptor, () => provider.BeginSafeState(null, 0), label, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 总线已经在跳闸，驱动异常不再升级，只记录并继续下一个模块
            _logger.LogCritical(ex, "安全跳闸：{Device} 的驱动 {Driver} 在下发安全状态时抛出异常，设备可能仍在输出或带载",
                binding.Device, binding.Device.Driver);
            return;
        }

        switch (outcome)
        {
            case ExchangeOutcome.Succeeded:
                _logger.LogWarning("安全跳闸：{Device} 已下发安全状态", binding.Device);
                break;

            case ExchangeOutcome.DeviceFailed failed:
                _logger.LogCritical("安全跳闸：{Device} 下发安全状态失败（{Kind}）：{Reason}，设备可能仍在输出或带载",
                    binding.Device, failed.Kind, failed.Reason);
                break;

            case ExchangeOutcome.BusUnavailable unavailable:
                // 超时以外的链路错误：同一通道上后面的模块多半也发不出去，但仍逐个尝试，由总时限兜底
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogCritical("安全跳闸：{Device} 下发安全状态时通道不可用：{Error}，设备可能仍在输出或带载",
                    binding.Device, unavailable.Error);
                break;
        }
    }
}
