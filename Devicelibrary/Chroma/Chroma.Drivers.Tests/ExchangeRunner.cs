using System.Text;
using Solve.Device.Sdk;

namespace Chroma.Drivers.Tests;

/// <summary>
/// 按 SDK 约定推进一次交互，代替内核的执行器：发送 → 设备应答 → 逐块 Match → OnFrame。
/// </summary>
internal static class ExchangeRunner
{
    public sealed record Run(ExchangeStep Final, IReadOnlyList<ExchangeStep.Send> Sent)
    {
        public IReadOnlyList<string> Commands => Sent.Select(s => Encoding.ASCII.GetString(s.Request.Span)).ToList();
    }

    /// <param name="device">模拟设备：收到一行命令（不含 LF），返回原样的响应字节文本；返回 null 表示不应答（超时）</param>
    /// <param name="chunk">每次交给 Match 的字节数，用于模拟分包</param>
    public static Run Execute(IExchange exchange, Func<string, string?> device, int chunk = int.MaxValue)
    {
        var sent = new List<ExchangeStep.Send>();
        var step = exchange.Start();
        while (step is ExchangeStep.Send send)
        {
            sent.Add(send);
            Assert.True(sent.Count <= 32, "交互步数超过内核上限");

            var request = Encoding.ASCII.GetString(send.Request.Span);
            Assert.EndsWith("\n", request);
            var reply = device(request[..^1]);

            if (!send.ExpectsResponse)
            {
                step = exchange.OnFrame([]);
                continue;
            }

            if (reply is null)
                return new Run(new ExchangeStep.Failed("响应超时", FailureKind.Transient), sent);

            var bytes = Encoding.ASCII.GetBytes(reply);
            var length = 0;
            var status = FrameStatus.NeedMoreData;
            var frameLength = 0;
            while (status == FrameStatus.NeedMoreData && length < bytes.Length)
            {
                length = Math.Min(bytes.Length, length + chunk);
                status = exchange.Match(bytes.AsSpan(0, length), out frameLength);
            }

            if (status != FrameStatus.Complete)
                return new Run(new ExchangeStep.Failed($"未成帧：{status}", FailureKind.Transient), sent);

            step = exchange.OnFrame(bytes.AsSpan(0, frameLength));
        }

        return new Run(step, sent);
    }
}
