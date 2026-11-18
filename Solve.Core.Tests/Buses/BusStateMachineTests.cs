using Solve.Core.Domain.Buses;

namespace Solve.Core.Tests.Buses;

public sealed class BusStateMachineTests
{
    // 规格表独立于生产代码手写一份：生产迁移表被误改时，穷举测试会逐格报出差异
    private static readonly Dictionary<(BusState From, BusTrigger Trigger), BusState> Specification = BuildSpecification();

    private static Dictionary<(BusState, BusTrigger), BusState> BuildSpecification()
    {
        var spec = new Dictionary<(BusState, BusTrigger), BusState>
        {
            [(BusState.Disconnected, BusTrigger.Connect)] = BusState.Connecting,

            [(BusState.Connecting, BusTrigger.OpenSucceeded)] = BusState.Connected,
            [(BusState.Connecting, BusTrigger.OpenFailed)] = BusState.Reconnecting,
            [(BusState.Connecting, BusTrigger.Disconnect)] = BusState.Disconnecting,

            [(BusState.Connected, BusTrigger.IoFailed)] = BusState.Reconnecting,
            [(BusState.Connected, BusTrigger.HeartbeatLost)] = BusState.Reconnecting,
            [(BusState.Connected, BusTrigger.Disconnect)] = BusState.Disconnecting,

            [(BusState.Reconnecting, BusTrigger.OpenSucceeded)] = BusState.Connected,
            [(BusState.Reconnecting, BusTrigger.OpenFailed)] = BusState.Reconnecting,
            [(BusState.Reconnecting, BusTrigger.RetryExhausted)] = BusState.Faulted,
            [(BusState.Reconnecting, BusTrigger.Disconnect)] = BusState.Disconnecting,

            [(BusState.Disconnecting, BusTrigger.Closed)] = BusState.Disconnected,

            [(BusState.Faulted, BusTrigger.Reset)] = BusState.Disconnected,
            [(BusState.SafeStop, BusTrigger.Reset)] = BusState.Disconnected,
        };

        foreach (var state in Enum.GetValues<BusState>())
            spec[(state, BusTrigger.SafetyTrip)] = BusState.SafeStop;

        return spec;
    }

    public static TheoryData<BusState, BusTrigger> AllCombinations()
    {
        var data = new TheoryData<BusState, BusTrigger>();
        foreach (var state in Enum.GetValues<BusState>())
        foreach (var trigger in Enum.GetValues<BusTrigger>())
            data.Add(state, trigger);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void TransitionTable_MatchesSpecification(BusState from, BusTrigger trigger)
    {
        var accepted = BusStateMachine.TryGetNext(from, trigger, out var to);

        if (Specification.TryGetValue((from, trigger), out var expected))
        {
            Assert.True(accepted, $"{from} + {trigger} 应当被接受");
            Assert.Equal(expected, to);
        }
        else
        {
            Assert.False(accepted, $"{from} + {trigger} 应当被拒绝，实际迁到 {to}");
        }
    }

    [Fact]
    public void UndefinedEnumValues_AreRejected()
    {
        Assert.False(BusStateMachine.TryGetNext((BusState)99, BusTrigger.SafetyTrip, out _));
        Assert.False(BusStateMachine.TryGetNext(BusState.Disconnected, (BusTrigger)99, out _));
    }

    [Fact]
    public void NewMachine_StartsDisconnected()
    {
        Assert.Equal(BusState.Disconnected, new BusStateMachine().State);
    }

    [Fact]
    public void Fire_ValidTrigger_ReturnsTransitionAndUpdatesState()
    {
        var machine = new BusStateMachine();

        var result = machine.Fire(BusTrigger.Connect);

        Assert.True(result.IsSuccess);
        Assert.Equal(new BusTransition(BusState.Disconnected, BusTrigger.Connect, BusState.Connecting), result.Value);
        Assert.False(result.Value.IsSelfTransition);
        Assert.Equal(BusState.Connecting, machine.State);
    }

    [Fact]
    public void Fire_InvalidTrigger_ReturnsErrorAndKeepsState()
    {
        var machine = new BusStateMachine();

        var result = machine.Fire(BusTrigger.OpenSucceeded);

        Assert.True(result.IsFailure);
        Assert.Equal(BusErrors.InvalidTransitionCode, result.Error.Code);
        Assert.Equal(BusState.Disconnected, machine.State);
    }

    [Fact]
    public void ReconnectExhausted_LatchesFaulted_UntilReset()
    {
        var machine = new BusStateMachine();
        FireAll(machine,
            BusTrigger.Connect, BusTrigger.OpenSucceeded,
            BusTrigger.IoFailed,
            BusTrigger.OpenFailed, BusTrigger.OpenFailed,
            BusTrigger.RetryExhausted);
        Assert.Equal(BusState.Faulted, machine.State);

        // 故障锁存：不允许绕过复位直接重连
        Assert.True(machine.Fire(BusTrigger.Connect).IsFailure);
        Assert.Equal(BusState.Faulted, machine.State);

        FireAll(machine, BusTrigger.Reset, BusTrigger.Connect);
        Assert.Equal(BusState.Connecting, machine.State);
    }

    [Fact]
    public void ReconnectAttemptFailure_IsSelfTransition()
    {
        var machine = new BusStateMachine();
        FireAll(machine, BusTrigger.Connect, BusTrigger.OpenFailed);

        var result = machine.Fire(BusTrigger.OpenFailed);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsSelfTransition);
    }

    [Fact]
    public void SafetyTrip_FromConnected_LocksIo_UntilReset()
    {
        var machine = new BusStateMachine();
        FireAll(machine, BusTrigger.Connect, BusTrigger.OpenSucceeded, BusTrigger.SafetyTrip);

        Assert.Equal(BusState.SafeStop, machine.State);
        Assert.False(BusStateMachine.AllowsIo(machine.State));
        Assert.True(machine.Fire(BusTrigger.Connect).IsFailure);
        Assert.True(machine.Fire(BusTrigger.Disconnect).IsFailure);

        // 重复跳闸幂等
        Assert.True(machine.Fire(BusTrigger.SafetyTrip).Value.IsSelfTransition);

        FireAll(machine, BusTrigger.Reset);
        Assert.Equal(BusState.Disconnected, machine.State);
    }

    [Fact]
    public void GracefulDisconnect_RequiresClosedConfirmation()
    {
        var machine = new BusStateMachine();
        FireAll(machine, BusTrigger.Connect, BusTrigger.OpenSucceeded, BusTrigger.Disconnect);
        Assert.Equal(BusState.Disconnecting, machine.State);

        FireAll(machine, BusTrigger.Closed);
        Assert.Equal(BusState.Disconnected, machine.State);
    }

    [Theory]
    [InlineData(BusState.Connected, true)]
    [InlineData(BusState.Disconnected, false)]
    [InlineData(BusState.Connecting, false)]
    [InlineData(BusState.Reconnecting, false)]
    [InlineData(BusState.Disconnecting, false)]
    [InlineData(BusState.Faulted, false)]
    [InlineData(BusState.SafeStop, false)]
    public void AllowsIo_OnlyWhenConnected(BusState state, bool expected)
    {
        Assert.Equal(expected, BusStateMachine.AllowsIo(state));
    }

    [Fact]
    public async Task ConcurrentFire_IsAtomic()
    {
        // 多个 I/O 调用方同时报告失败：只能有一个把 Connected 迁到 Reconnecting，其余必须被拒绝
        var machine = new BusStateMachine();
        FireAll(machine, BusTrigger.Connect, BusTrigger.OpenSucceeded);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait(TimeSpan.FromSeconds(5));
                return machine.Fire(BusTrigger.IoFailed).IsSuccess;
            }))
            .ToArray();
        start.Set();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(BusState.Reconnecting, machine.State);
    }

    private static void FireAll(BusStateMachine machine, params BusTrigger[] triggers)
    {
        foreach (var trigger in triggers)
        {
            var result = machine.Fire(trigger);
            Assert.True(result.IsSuccess, $"前置步骤 {trigger} 失败：{(result.IsFailure ? result.Error : null)}");
        }
    }
}
