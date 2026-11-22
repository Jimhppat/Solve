using Chroma.Drivers.Common;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// 一个 62000B 模块的会话。内核保证同一会话的调用串行进行。
/// </summary>
/// <remarks>
/// 型号确认：上一次交互没有成功结束（首次、超时、断线、设备被更换）时，下一次交互先发 *IDN? 确认型号，
/// 确认通过才继续读数或执行命令。这样型号检查不占用正常轮询的带宽，又能在通信恢复后重新核对。
/// </remarks>
internal sealed class Chroma62000BSession(Chroma62000BModel model, TimeSpan commandDelay) : IDeviceSession, ISafeStateProvider
{
    private IChromaExchange? _last;

    public IExchange BeginPoll() => Track(new PollExchange(model, VerifyIdentityNext));

    public IExchange BeginCommand(DeviceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Key switch
        {
            Chroma62000BDriver.OutputOnKey => Track(new OutputExchange(model, on: true, commandDelay, VerifyIdentityNext)),
            Chroma62000BDriver.OutputOffKey => Track(new OutputExchange(model, on: false, commandDelay, VerifyIdentityNext)),

            // 不支持的命令重试也没用：标为配置错误，且不发送任何字节
            _ => new FailedExchange($"Chroma {model.Model} 不支持命令 {command.Key}。", FailureKind.Configuration),
        };
    }

    /// <summary>
    /// 安全状态：CONF:OUTP OFF → 等待 → CONF:OUTP? 回读确认。单路设备，整机与通道 1 相同。
    /// </summary>
    /// <remarks>
    /// 不先确认型号：跳闸时通信状态未知，确认失败会让关输出也发不出去，而关输出对任何设备都是安全方向。
    /// 也正因为没有确认型号，这次交互不算作"成功的上一次交互"：之后的第一次交互重新确认型号
    /// （跳闸后到复位之间设备可能被更换）。
    /// </remarks>
    public IExchange BeginSafeState(string? group, int channel)
    {
        var whole = group is null && channel == 0;
        var single = string.Equals(group, Chroma62000BDriver.OutputChannelKey, StringComparison.Ordinal) && channel == 1;
        if (!whole && !single)
            return new FailedExchange($"Chroma {model.Model} 没有通道 {group ?? "（整机）"} {channel}，无法执行安全停止。", FailureKind.Configuration);

        _last = null;
        return new OutputExchange(model, on: false, commandDelay, verifyIdentity: false);
    }

    private bool VerifyIdentityNext => _last is not { Succeeded: true };

    private IExchange Track(IChromaExchange exchange)
    {
        _last = exchange;
        return exchange;
    }
}

/// <summary>
/// 会话需要知道一次交互是否成功结束，以决定下一次是否重新确认型号
/// </summary>
internal interface IChromaExchange : IExchange
{
    bool Succeeded { get; }
}
