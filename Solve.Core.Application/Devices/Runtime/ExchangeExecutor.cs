using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices.Runtime;

/// <summary>
/// 一次报文交互的结果
/// </summary>
public abstract record ExchangeOutcome
{
    private ExchangeOutcome()
    {
    }

    /// <summary>
    /// 交互成功，带回点位值（命令交互通常为空）
    /// </summary>
    public sealed record Succeeded(IReadOnlyList<PointSample> Samples) : ExchangeOutcome;

    /// <summary>
    /// 与这个模块的交互失败（响应超时、帧无效、驱动报告失败），按 <see cref="Kind"/> 决定是否重试
    /// </summary>
    public sealed record DeviceFailed(string Reason, FailureKind Kind) : ExchangeOutcome;

    /// <summary>
    /// 总线本身不可用（未连接、通道被占用、链路故障、事务超时），与具体模块无关，不计入模块的失败次数
    /// </summary>
    public sealed record BusUnavailable(Error Error) : ExchangeOutcome;
}

/// <summary>
/// 报文交互执行器：在总线的一个事务（通道锁）内逐步推进驱动的 <see cref="IExchange"/>，
/// 负责帧间隔、发送、按超时接收、拆帧、丢弃多余字节、帧后等待，并把每一步的原始报文记入追溯日志。
/// </summary>
/// <remarks>
/// <para>整个交互在同一个事务里完成：多步交互不会被其他模块的报文插入。事务总时长受总线运行时的事务超时约束。</para>
/// <para>驱动方法抛出的异常不在这里捕获：按 SDK 约定视为通信状态未知，由总线运行时安全跳闸后原样抛出，调用方负责记录。</para>
/// <para>计时：Windows 上 Task.Delay 的精度约 15 ms，毫秒级的帧间隔实际会更长；更长的静默对主从协议无害，只会略降吞吐。</para>
/// </remarks>
public sealed class ExchangeExecutor
{
    /// <summary>
    /// 一次交互最多的发送步数，防止驱动的状态机写错时无限占用总线
    /// </summary>
    public const int MaxSteps = 32;

    /// <summary>
    /// 一帧响应最多累计的字节数，超过仍未成帧即判为无效，防止驱动的 Match 永远返回"还不够"时无限缓存
    /// </summary>
    public const int MaxResponseBytes = 4096;

    private readonly ILogger _logger;

    public ExchangeExecutor(ILogger<ExchangeExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <param name="label">日志中标识这次交互的文本，如"模块 电源-01（地址 1）轮询"</param>
    public async Task<ExchangeOutcome> ExecuteAsync(
        BusRuntime bus,
        IExchange exchange,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(exchange);

        var descriptor = bus.Descriptor;
        var result = await bus.ExecuteAsync(
            (channel, tx) => RunAsync(channel, exchange, descriptor, label, tx),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? result.Value : new ExchangeOutcome.BusUnavailable(result.Error);
    }

    private async ValueTask<Result<ExchangeOutcome>> RunAsync(
        IBusChannel channel,
        IExchange exchange,
        BusDescriptor descriptor,
        string label,
        CancellationToken tx)
    {
        var step = exchange.Start();
        for (var count = 0; step is ExchangeStep.Send send; count++)
        {
            if (count >= MaxSteps)
                return Device($"交互超过 {MaxSteps} 步仍未结束，驱动的交互状态机可能有误。", FailureKind.Configuration);

            if (CheckLimits(send, descriptor) is { } invalid)
                return Device(invalid, FailureKind.Configuration);

            // 清掉上一次交互超时后迟到的残留数据；SDK 1 不支持主动上报，请求之外到达的数据一律丢弃
            var discarded = await channel.DiscardInputAsync(tx).ConfigureAwait(false);
            if (discarded.IsFailure)
                return Bus(discarded.Error);

            if (GapDuration(send.GapBefore, descriptor) is { } gap && gap > TimeSpan.Zero)
                await Task.Delay(gap, tx).ConfigureAwait(false);

            var written = await channel.WriteAsync(send.Request, tx).ConfigureAwait(false);
            if (written.IsFailure)
                return Bus(written.Error);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{Label} 发送 {Bytes}", label, Convert.ToHexString(send.Request.Span));

            byte[] frame;
            if (send.ExpectsResponse)
            {
                var received = await ReceiveFrameAsync(channel, exchange, send, descriptor, label, tx).ConfigureAwait(false);
                if (received.Outcome is { } failed)
                    return Result<ExchangeOutcome>.Success(failed);

                frame = received.Frame!;
            }
            else
            {
                frame = [];
            }

            if (send.DelayAfter > TimeSpan.Zero)
                await Task.Delay(send.DelayAfter, tx).ConfigureAwait(false);

            step = exchange.OnFrame(frame);
        }

        return step switch
        {
            ExchangeStep.Completed completed => Result<ExchangeOutcome>.Success(new ExchangeOutcome.Succeeded(completed.Samples ?? [])),
            ExchangeStep.Failed failed => Device(failed.Reason, Enum.IsDefined(failed.Kind) ? failed.Kind : FailureKind.Transient),
            _ => Device("驱动返回了空的交互步骤。", FailureKind.Configuration),
        };
    }

    private readonly record struct Received(byte[]? Frame, ExchangeOutcome? Outcome);

    /// <summary>
    /// 在响应超时内接收，每收到一块就交给驱动判断是否成帧
    /// </summary>
    private async ValueTask<Received> ReceiveFrameAsync(
        IBusChannel channel,
        IExchange exchange,
        ExchangeStep.Send send,
        BusDescriptor descriptor,
        string label,
        CancellationToken tx)
    {
        var timeout = send.ResponseTimeout ?? descriptor.Timeouts.Read;
        var buffer = new byte[MaxResponseBytes];
        var length = 0;
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return Timeout(timeout, buffer.AsSpan(0, length), label);

            Result<int> read;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(tx))
            {
                deadline.CancelAfter(remaining);
                try
                {
                    read = await channel.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !tx.IsCancellationRequested)
                {
                    return Timeout(timeout, buffer.AsSpan(0, length), label);
                }
            }

            if (read.IsFailure)
            {
                // 单次读超时只说明这段时间没数据；本帧的响应超时还没到就继续等
                if (read.Error.Code == BusErrors.TimeoutCode && watch.Elapsed < timeout)
                    continue;

                return new Received(null, new ExchangeOutcome.BusUnavailable(read.Error));
            }

            length += read.Value;
            var status = exchange.Match(buffer.AsSpan(0, length), out var frameLength);
            switch (status)
            {
                case FrameStatus.NeedMoreData when length < buffer.Length:
                    continue;

                case FrameStatus.NeedMoreData:
                    LogRaw(LogLevel.Warning, label, "响应超过上限仍未成帧", buffer.AsSpan(0, length));
                    return new Received(null, new ExchangeOutcome.DeviceFailed($"响应超过 {MaxResponseBytes} 字节仍未成帧。", FailureKind.Transient));

                case FrameStatus.Complete when frameLength >= 1 && frameLength <= length:
                    if (frameLength < length)
                        LogRaw(LogLevel.Information, label, "响应后多余数据（已丢弃）", buffer.AsSpan(frameLength, length - frameLength));

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("{Label} 接收 {Bytes}", label, Convert.ToHexString(buffer.AsSpan(0, frameLength)));

                    return new Received(buffer.AsSpan(0, frameLength).ToArray(), null);

                case FrameStatus.Complete:
                    return new Received(null, new ExchangeOutcome.DeviceFailed(
                        $"驱动给出的帧长度 {frameLength} 无效（已收到 {length} 字节）。", FailureKind.Configuration));

                default:
                    LogRaw(LogLevel.Warning, label, "响应无效", buffer.AsSpan(0, length));
                    return new Received(null, new ExchangeOutcome.DeviceFailed("响应无效（帧头、地址或校验不符）。", FailureKind.Transient));
            }
        }
    }

    private Received Timeout(TimeSpan timeout, ReadOnlySpan<byte> partial, string label)
    {
        if (partial.Length > 0)
            LogRaw(LogLevel.Warning, label, "响应超时，已收到部分数据", partial);

        return new Received(null, new ExchangeOutcome.DeviceFailed(
            partial.Length == 0
                ? $"响应超时（{timeout.TotalMilliseconds:0} ms 内无数据）。"
                : $"响应超时（{timeout.TotalMilliseconds:0} ms 内只收到 {partial.Length} 字节）。",
            FailureKind.Transient));
    }

    private void LogRaw(LogLevel level, string label, string what, ReadOnlySpan<byte> bytes)
    {
        if (_logger.IsEnabled(level))
            _logger.Log(level, "{Label} {What}：{Bytes}", label, what, Convert.ToHexString(bytes));
    }

    /// <summary>
    /// 帧间隔：串口取 max(字符数 × 字符时间, 最小时长)；TCP 只取最小时长
    /// </summary>
    public static TimeSpan? GapDuration(FrameGap? gap, BusDescriptor descriptor)
    {
        if (gap is not { } g)
            return null;

        if (descriptor is not SerialBusDescriptor serial)
            return g.Minimum;

        var characters = TimeSpan.FromSeconds(g.Characters * BitsPerCharacter(serial) / serial.BaudRate);
        return characters > g.Minimum ? characters : g.Minimum;
    }

    /// <summary>
    /// 一个字符在线上的位数：起始位 + 数据位 + 校验位 + 停止位
    /// </summary>
    public static double BitsPerCharacter(SerialBusDescriptor serial)
        => 1 + serial.DataBits + (serial.Parity == SerialParity.None ? 0 : 1) + serial.StopBits switch
        {
            SerialStopBits.Two => 2,
            SerialStopBits.OnePointFive => 1.5,
            _ => 1,
        };

    private static string? CheckLimits(ExchangeStep.Send send, BusDescriptor descriptor)
    {
        if (send.Request.IsEmpty)
            return "驱动要发送的请求为空。";
        if (send.GapBefore is { } gap && (gap.Characters is < 0 or > ExchangeLimits.MaxGapCharacters
                                          || gap.Minimum < TimeSpan.Zero || gap.Minimum > ExchangeLimits.MaxGapMinimum))
            return "驱动要求的帧间隔超出允许范围。";
        if (send.DelayAfter < TimeSpan.Zero || send.DelayAfter > ExchangeLimits.MaxDelayAfter)
            return "驱动要求的帧后等待超出允许范围。";
        if (send.ResponseTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > ExchangeLimits.MaxResponseTimeout))
            return "驱动要求的响应超时超出允许范围。";

        return null;
    }

    private static Result<ExchangeOutcome> Device(string reason, FailureKind kind)
        => Result<ExchangeOutcome>.Success(new ExchangeOutcome.DeviceFailed(reason, kind));

    private static Result<ExchangeOutcome> Bus(Error error)
        => Result<ExchangeOutcome>.Success(new ExchangeOutcome.BusUnavailable(error));
}
