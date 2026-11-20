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
internal sealed class Chroma62000BSession(Chroma62000BModel model, TimeSpan commandDelay) : IDeviceSession
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
