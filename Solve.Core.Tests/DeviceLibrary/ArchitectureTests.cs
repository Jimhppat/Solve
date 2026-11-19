using System.Reflection;
using Solve.Adapters.DeviceLibrary;
using Solve.Core.Application.Buses;
using Solve.Core.Domain.Buses;
using Solve.Device.Sdk;

namespace Solve.Core.Tests.DeviceLibrary;

/// <summary>
/// 守住"驱动不写进内核"的边界：驱动包只认 SDK，内核只通过设备库适配器接触驱动包。
/// </summary>
public sealed class ArchitectureTests
{
    private static IEnumerable<string> SolveReferences(Assembly assembly)
        => assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Solve.", StringComparison.Ordinal));

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
    public void KernelCore_DoesNotReferenceSdkOrDriverPackages()
    {
        foreach (var kernel in new[] { typeof(BusId).Assembly, typeof(BusRuntime).Assembly })
        {
            var references = kernel.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain("Solve.Device.Sdk", references);
            Assert.DoesNotContain(references, r => r.StartsWith("Sample.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void OnlyDeviceLibraryAdapter_ReferencesSdk()
    {
        var referencing = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name!.StartsWith("Solve.", StringComparison.Ordinal))
            .Where(a => a.GetReferencedAssemblies().Any(r => r.Name == "Solve.Device.Sdk"))
            .Select(a => a.GetName().Name!)
            .Where(n => n != "Solve.Core.Tests")
            .ToList();

        Assert.All(referencing, name => Assert.Equal(typeof(DeviceLibraryManager).Assembly.GetName().Name, name));
    }
}
