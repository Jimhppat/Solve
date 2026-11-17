using Solve.Adapters.Hardware.Buses.Simulation;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses;

public sealed class SimulatedTransportTests : IAsyncDisposable
{
    // 测试自身的看门狗：任何等待都不得无限挂起
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(5);

    private readonly SimulatedTransport _transport = new(BusId.New());

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private static CancellationToken Guard() => new CancellationTokenSource(TestGuard).Token;

    private async Task OpenAsync()
        => Assert.True((await _transport.OpenAsync(Guard())).IsSuccess);

    private async Task<byte[]> ReadOnceAsync(int bufferSize = 64, CancellationToken? token = null)
    {
        var buffer = new byte[bufferSize];
        var result = await _transport.ReadAsync(buffer, token ?? Guard());
        Assert.True(result.IsSuccess, result.ToString());
        return buffer[..result.Value];
    }

    // ---------- 生命周期 ----------

    [Fact]
    public async Task OpenClose_CanCycle_AndCloseIsIdempotent()
    {
        Assert.False(_transport.IsOpen);

        await OpenAsync();
        Assert.True(_transport.IsOpen);

        await _transport.CloseAsync(Guard());
        await _transport.CloseAsync(Guard());
        Assert.False(_transport.IsOpen);

        await OpenAsync();
        Assert.True(_transport.IsOpen);
    }

    [Fact]
    public async Task OpenTwice_ReturnsAlreadyOpen()
    {
        await OpenAsync();

        var result = await _transport.OpenAsync(Guard());

        Assert.Equal(BusErrors.AlreadyOpenCode, result.Error.Code);
    }

    [Fact]
    public async Task IoWhenClosed_ReturnsNotOpen()
    {
        Assert.Equal(BusErrors.NotOpenCode, (await _transport.WriteAsync(new byte[] { 1 }, Guard())).Error.Code);
        Assert.Equal(BusErrors.NotOpenCode, (await _transport.ReadAsync(new byte[1], Guard())).Error.Code);
        Assert.Equal(BusErrors.NotOpenCode, (await _transport.DiscardInputAsync(Guard())).Error.Code);
    }

    [Fact]
    public async Task OpenDelay_HonorsCancellation()
    {
        _transport.OpenDelay = TimeSpan.FromSeconds(30);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _transport.OpenAsync(timeout.Token).AsTask());
        Assert.False(_transport.IsOpen);
    }

    // ---------- 收发语义 ----------

    [Fact]
    public async Task Responder_EchoesRequest()
    {
        _transport.Responder = request => request.ToArray();
        await OpenAsync();

        Assert.True((await _transport.WriteAsync(new byte[] { 0x01, 0x03, 0x00 }, Guard())).IsSuccess);

        Assert.Equal([0x01, 0x03, 0x00], await ReadOnceAsync());
        Assert.Equal([0x01, 0x03, 0x00], Assert.Single(_transport.GetTxHistory()));
    }

    [Fact]
    public async Task Read_IsStreamSemantics_PartialAndPerChunk()
    {
        await OpenAsync();
        Assert.True(_transport.InjectRx([1, 2, 3, 4, 5]));
        Assert.True(_transport.InjectRx([6, 7]));

        // 半包：小缓冲区分多次读出
        Assert.Equal([1, 2], await ReadOnceAsync(bufferSize: 2));
        Assert.Equal([3, 4], await ReadOnceAsync(bufferSize: 2));
        Assert.Equal([5], await ReadOnceAsync(bufferSize: 2));

        // 粘包场景下一次读取不保证边界，协议层必须自行做帧同步
        Assert.Equal([6, 7], await ReadOnceAsync());
    }

    [Fact]
    public async Task SilentDevice_ReadTimesOutViaCancellation()
    {
        _transport.Responder = _ => null;
        await OpenAsync();
        await _transport.WriteAsync(new byte[] { 0x01 }, Guard());

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _transport.ReadAsync(new byte[8], timeout.Token).AsTask());
    }

    [Fact]
    public async Task LateResponse_ArrivesAfterReadTimeout_AsStaleData()
    {
        _transport.Responder = request => request.ToArray();
        _transport.ResponseDelay = TimeSpan.FromMilliseconds(150);
        await OpenAsync();
        await _transport.WriteAsync(new byte[] { 0xAA }, Guard());

        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _transport.ReadAsync(new byte[8], timeout.Token).AsTask());
        }

        // 超时后迟到的响应仍会进入接收缓冲区，这正是协议层发送前必须 DiscardInput 的原因
        Assert.Equal([0xAA], await ReadOnceAsync());
    }

    [Fact]
    public async Task DiscardInput_ClearsQueuedAndPartiallyReadData()
    {
        await OpenAsync();
        Assert.True(_transport.InjectRx([1, 2, 3]));
        Assert.True(_transport.InjectRx([4]));
        Assert.Equal([1], await ReadOnceAsync(bufferSize: 1)); // 留下半块 [2, 3]

        Assert.True((await _transport.DiscardInputAsync(Guard())).IsSuccess);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _transport.ReadAsync(new byte[8], timeout.Token).AsTask());
    }

    [Fact]
    public async Task LateResponse_FromPreviousConnection_IsDropped()
    {
        _transport.Responder = request => request.ToArray();
        _transport.ResponseDelay = TimeSpan.FromMilliseconds(80);
        await OpenAsync();
        await _transport.WriteAsync(new byte[] { 0x55 }, Guard());

        await _transport.CloseAsync(Guard());
        await OpenAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _transport.ReadAsync(new byte[8], timeout.Token).AsTask());
        Assert.Equal(1, _transport.DroppedChunks);
    }

    [Fact]
    public async Task RxOverflow_IsCountedNotBlocking()
    {
        await OpenAsync();

        for (var i = 0; i < SimulatedTransport.RxQueueCapacity; i++)
            Assert.True(_transport.InjectRx([(byte)i]));

        Assert.False(_transport.InjectRx([0xFF]));
        Assert.Equal(1, _transport.DroppedChunks);
    }

    [Fact]
    public async Task TxHistory_IsBounded()
    {
        await OpenAsync();

        for (var i = 0; i < SimulatedTransport.TxHistoryCapacity + 10; i++)
            await _transport.WriteAsync(new[] { (byte)i }, Guard());

        var history = _transport.GetTxHistory();
        Assert.Equal(SimulatedTransport.TxHistoryCapacity, history.Count);
        Assert.Equal([(byte)10], history[0]);
    }

    // ---------- 故障注入 ----------

    [Fact]
    public async Task DropConnection_WakesPendingReadWithConnectionClosed()
    {
        await OpenAsync();
        var pendingRead = _transport.ReadAsync(new byte[8], Guard()).AsTask();

        _transport.DropConnection();

        var result = await pendingRead.WaitAsync(TestGuard);
        Assert.Equal(BusErrors.ConnectionClosedCode, result.Error.Code);
        Assert.False(_transport.IsOpen);
    }

    [Fact]
    public async Task InjectedFaults_AreOneShot()
    {
        _transport.FailNextOpen();
        Assert.Equal(BusErrors.OpenFailedCode, (await _transport.OpenAsync(Guard())).Error.Code);
        await OpenAsync();

        _transport.FailNextWrite();
        Assert.Equal(BusErrors.IoFailedCode, (await _transport.WriteAsync(new byte[] { 1 }, Guard())).Error.Code);
        Assert.True((await _transport.WriteAsync(new byte[] { 1 }, Guard())).IsSuccess);

        var custom = new Error("Test.Custom", "自定义故障");
        _transport.FailNextRead(custom);
        Assert.Same(custom, (await _transport.ReadAsync(new byte[1], Guard())).Error);
    }

    // ---------- 释放 ----------

    [Fact]
    public async Task Dispose_IsIdempotent_WakesReader_AndRejectsFurtherUse()
    {
        await OpenAsync();
        var pendingRead = _transport.ReadAsync(new byte[8], Guard()).AsTask();

        await _transport.DisposeAsync();
        await _transport.DisposeAsync();

        Assert.Equal(BusErrors.ConnectionClosedCode, (await pendingRead.WaitAsync(TestGuard)).Error.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _transport.OpenAsync(Guard()).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _transport.WriteAsync(new byte[] { 1 }, Guard()).AsTask());
    }

    [Fact]
    public async Task Dispose_CancelsPendingDelayedResponses()
    {
        _transport.Responder = request => request.ToArray();
        _transport.ResponseDelay = TimeSpan.FromSeconds(30);
        await OpenAsync();
        await _transport.WriteAsync(new byte[] { 1 }, Guard());

        await _transport.DisposeAsync();

        // 延迟任务被取消后计入丢弃；轮询等待，最长受测试看门狗约束
        using var guard = new CancellationTokenSource(TestGuard);
        while (_transport.DroppedChunks == 0)
            await Task.Delay(10, guard.Token);

        Assert.Equal(1, _transport.DroppedChunks);
    }
}
