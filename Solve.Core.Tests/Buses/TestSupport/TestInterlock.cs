using Solve.Core.Application.Buses.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Core.Tests.Buses.TestSupport;

/// <summary>
/// 可控的安全联锁替身：Active = true 时禁止从 SafeStop 复位
/// </summary>
internal sealed class TestInterlock : IBusSafetyInterlock
{
    private volatile bool _active;

    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public Result CanReset(BusId busId)
        => Active ? Result.Failure(BusErrors.InterlockActive("测试联锁")) : Result.Success();
}
