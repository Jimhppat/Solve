using System.Reflection;
using Solve.Adapters.DeviceLibrary;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.DeviceLibrary;

/// <summary>
/// 守住"驱动不写进内核"的边界：
/// SDK 不依赖任何 Solve.* 程序集；领域层不认识 SDK；应用层只认 SDK 这份契约；
/// 任何 Solve.* 程序集都不引用协议包或驱动包；驱动包只能引用 SDK。
/// </summary>
public sealed class ArchitectureTests
{
    private static IEnumerable<string> SolveReferences(Assembly assembly)
        => assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Solve.", StringComparison.Ordinal));

    private static IEnumerable<Assembly> SolveAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name!.StartsWith("Solve.", StringComparison.Ordinal))
            .Where(a => a.GetName().Name != "Solve.Core.Tests");

    [Fact]
    public void Sdk_DependsOnNoSolveAssembly()
        => Assert.Empty(SolveReferences(typeof(IDriverPackage).Assembly));

    [Fact]
    public void SampleDriverPackage_DependsOnlyOnSdk()
    {
        var sample = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Sample.DriverPackage.dll"));

        Assert.Equal(["Solve.Device.Sdk"], SolveReferences(sample));
    }

    [Fact]
    public void Domain_DoesNotReferenceSdk()
        => Assert.DoesNotContain("Solve.Device.Sdk", typeof(BusId).Assembly.GetReferencedAssemblies().Select(a => a.Name));

    [Fact]
    public void Application_ReferencesOnlyDomainAndSdk()
        => Assert.Equal(
            ["Solve.Core.Domain", "Solve.Device.Sdk"],
            SolveReferences(typeof(BusRuntime).Assembly).Order(StringComparer.Ordinal));

    /// <summary>
    /// SDK 每追加一个分类，内核都必须同时追加映射：否则声明了新分类的驱动包会整体判为不兼容
    /// </summary>
    [Fact]
    public void EverySdkCategory_MapsToKernelCategory()
    {
        foreach (var category in Enum.GetValues<DeviceCategory>())
            Assert.True(CategoryMap.TryMap(category, out _), $"SDK 分类 {category} 没有对应的内核分类");
    }

    /// <summary>
    /// 通道功能同理，且数值写进配置库（接线表的 kind 列），映射必须保持数值一致
    /// </summary>
    [Fact]
    public void EverySdkChannelFunction_MapsToKernelKind_WithSameValue()
    {
        foreach (var function in Enum.GetValues<ChannelFunction>())
        {
            Assert.True(ChannelKindMap.TryMap(function, out var kind), $"SDK 通道功能 {function} 没有对应的内核功能");
            Assert.Equal((int)function, (int)kind);
        }
    }

    /// <summary>
    /// 仿真设备只经应用层的分派接到仿真通道上：通道层只转交字节，不认识驱动与仿真契约，
    /// 实机通道因此没有任何途径调用驱动的仿真代码
    /// </summary>
    [Fact]
    public void TransportAdapters_DoNotReferenceSdk()
        => Assert.DoesNotContain("Solve.Device.Sdk",
            typeof(Solve.Adapters.Hardware.Buses.HardwareTransportFactory).Assembly.GetReferencedAssemblies().Select(a => a.Name));

    /// <summary>
    /// 内核里实现仿真设备的只能是测试替身：协议知识留在驱动包里
    /// </summary>
    [Fact]
    public void NoSolveAssembly_ImplementsSimulatedDevice()
    {
        foreach (var assembly in SolveAssemblies())
        {
            var implementations = assembly.GetTypes()
                .Where(t => typeof(ISimulatedDevice).IsAssignableFrom(t) || typeof(IDeviceSimulatorProvider).IsAssignableFrom(t))
                .Where(t => !t.IsInterface);
            Assert.Empty(implementations);
        }
    }

    [Fact]
    public void NoSolveAssembly_ReferencesProtocolOrDriverPackages()
    {
        foreach (var assembly in SolveAssemblies())
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain(references, r => r.StartsWith("Sample.", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("Chroma.", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("GJ.", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("Solve.Device.Protocols.", StringComparison.Ordinal));
        }
    }
}
