using System.Reflection;
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

    [Fact]
    public void NoSolveAssembly_ReferencesProtocolOrDriverPackages()
    {
        foreach (var assembly in SolveAssemblies())
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain(references, r => r.StartsWith("Sample.", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("Solve.Device.Protocols.", StringComparison.Ordinal));
        }
    }
}
