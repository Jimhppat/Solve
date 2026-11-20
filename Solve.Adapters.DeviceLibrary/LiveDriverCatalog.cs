using System.Diagnostics.CodeAnalysis;
using Solve.Core.Application.Drivers;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 运行中可变的驱动目录：内部持有一份不可变快照，变化时整份替换后触发 <see cref="Changed"/>。
/// </summary>
/// <remarks>
/// 读取无锁：每次调用读到某一份完整快照，不会看到"半个驱动包"。替换只由设备库在其互斥区内进行。
/// </remarks>
public sealed class LiveDriverCatalog : IDriverCatalog
{
    private DriverCatalog _current;

    public LiveDriverCatalog(DriverCatalog initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// 当前快照
    /// </summary>
    public DriverCatalog Current => Volatile.Read(ref _current);

    public IReadOnlyList<DriverDescriptor> All => Current.All;

    public bool TryGet(DriverId id, [NotNullWhen(true)] out DriverDescriptor? driver) => Current.TryGet(id, out driver);

    public bool TryGetImplementation(DriverId id, [NotNullWhen(true)] out IDeviceDriver? implementation)
        => Current.TryGetImplementation(id, out implementation);

    /// <summary>
    /// 整份替换并通知订阅者。订阅者只做轻量工作（投递信号），这里不吞异常：订阅者出错是编程错误，应当暴露。
    /// </summary>
    internal void Replace(DriverCatalog next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
