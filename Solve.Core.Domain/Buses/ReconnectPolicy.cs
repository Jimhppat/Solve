using Solve.Core.Domain.Common;

namespace Solve.Core.Domain.Buses;

/// <summary>
/// 自动重连策略（指数退避，纯计算，不计时不 I/O）。
/// </summary>
/// <param name="InitialDelay">第 1 次重连前的等待</param>
/// <param name="MaxDelay">退避等待上限</param>
/// <param name="Multiplier">每次失败后的等待倍率，取值 [1, 10]</param>
/// <param name="MaxAttempts">
/// 首次连接失败（或运行中断线）之后允许的重连次数。0 表示不自动重连直接 Faulted；
/// null 表示无限重连、永不进入 Faulted（仅在设备允许无人值守长时间离线时使用）。
/// </param>
/// <remarks>
/// 运行时用法：进入 Reconnecting 时 attemptsMade = 0；
/// 每轮先判 <see cref="IsExhausted"/>，为真则触发 RetryExhausted 进入 Faulted 锁存，
/// 否则等待 <see cref="GetDelay"/>(attemptsMade + 1) 后重试，失败则 attemptsMade++。
/// </remarks>
public sealed record ReconnectPolicy(TimeSpan InitialDelay, TimeSpan MaxDelay, double Multiplier, int? MaxAttempts)
{
    /// <summary>
    /// 退避上限的上限，防止配置错误导致总线"看似重连中、实则几小时才试一次"
    /// </summary>
    public static readonly TimeSpan MaxDelayLimit = TimeSpan.FromMinutes(10);

    public const double MinMultiplier = 1.0;
    public const double MaxMultiplier = 10.0;

    /// <summary>
    /// 默认：0.5 s 起步，翻倍退避，封顶 30 s，最多重连 5 次后 Faulted 锁存
    /// </summary>
    public static ReconnectPolicy Default { get; } = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(30),
        2.0,
        5);

    /// <summary>
    /// 是否已耗尽重连次数
    /// </summary>
    public bool IsExhausted(int attemptsMade)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptsMade);
        return MaxAttempts is { } max && attemptsMade >= max;
    }

    /// <summary>
    /// 第 <paramref name="attempt"/> 次重连（从 1 开始）前应等待的时间
    /// </summary>
    public TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        // 在 double 上计算再夹紧：attempt 很大时 Math.Pow 会溢出为 Infinity，直接构造 TimeSpan 会抛异常
        var milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);
        if (!double.IsFinite(milliseconds) || milliseconds >= MaxDelay.TotalMilliseconds)
            return MaxDelay;

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    internal void CollectErrors(List<Error> errors)
    {
        if (InitialDelay <= TimeSpan.Zero)
            errors.Add(BusErrors.InvalidField("Reconnect.InitialDelay", $"InitialDelay 必须大于 0，当前为 {InitialDelay}。"));

        if (MaxDelay <= TimeSpan.Zero || MaxDelay > MaxDelayLimit)
            errors.Add(BusErrors.InvalidField("Reconnect.MaxDelay", $"MaxDelay 必须在 (0, {MaxDelayLimit.TotalMinutes} min] 之间，当前为 {MaxDelay}。"));
        else if (InitialDelay > MaxDelay)
            errors.Add(BusErrors.InvalidField("Reconnect.MaxDelay", $"MaxDelay ({MaxDelay}) 不能小于 InitialDelay ({InitialDelay})。"));

        // NaN 与任何数比较都为 false，必须显式用 IsFinite 拦截
        if (!double.IsFinite(Multiplier) || Multiplier < MinMultiplier || Multiplier > MaxMultiplier)
            errors.Add(BusErrors.InvalidField("Reconnect.Multiplier", $"Multiplier 必须在 [{MinMultiplier}, {MaxMultiplier}] 之间，当前为 {Multiplier}。"));

        if (MaxAttempts < 0)
            errors.Add(BusErrors.InvalidField("Reconnect.MaxAttempts", $"MaxAttempts 不能为负数，当前为 {MaxAttempts}。"));
    }
}
