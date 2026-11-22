using Solve.Device.Sdk;

namespace GJ.Drivers.Tests;

/// <summary>
/// 按 SDK 约定推进一次交互，代替内核的执行器：发送 → 设备应答 → 逐块 Match → OnFrame。
/// </summary>
internal static class ExchangeRunner
{
    public sealed record Run(ExchangeStep Final, IReadOnlyList<byte[]> Sent);

    /// <param name="device">模拟设备：收到完整请求帧，返回原样的应答字节；返回 null 表示不应答（超时）</param>
    /// <param name="chunk">每次交给 Match 的字节数，用于模拟分包</param>
    public static Run Execute(IExchange exchange, Func<byte[], byte[]?> device, int chunk = int.MaxValue)
    {
        var sent = new List<byte[]>();
        var step = exchange.Start();
        while (step is ExchangeStep.Send send)
        {
            var request = send.Request.ToArray();
            sent.Add(request);
            Assert.True(sent.Count <= 32, "交互步数超过内核上限");

            var reply = device(request);
            if (!send.ExpectsResponse)
            {
                step = exchange.OnFrame([]);
                continue;
            }

            if (reply is null)
                return new Run(new ExchangeStep.Failed("响应超时", FailureKind.Transient), sent);

            var length = 0;
            var status = FrameStatus.NeedMoreData;
            var frameLength = 0;
            while (status == FrameStatus.NeedMoreData && length < reply.Length)
            {
                length = Math.Min(reply.Length, length + chunk);
                status = exchange.Match(reply.AsSpan(0, length), out frameLength);
            }

            if (status != FrameStatus.Complete)
                return new Run(new ExchangeStep.Failed($"未成帧：{status}", FailureKind.Transient), sent);

            step = exchange.OnFrame(reply.AsSpan(0, frameLength));
        }

        return new Run(step, sent);
    }
}
