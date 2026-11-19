using System.Reflection;
using System.Runtime.Loader;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 一个驱动包的程序集加载上下文。包内程序集一律从扫描时读入并计算过指纹的字节加载（不再读磁盘、不锁文件）。
/// </summary>
/// <remarks>
/// <para>共享边界：SDK 程序集交给默认上下文解析，驱动与内核看到的是同一份契约类型；
/// .NET 基础库同样交给默认上下文。不同包各自携带的依赖（如协议库）互不干扰，可以是不同版本。</para>
/// <para>驱动包只能依赖 SDK：引用内核的其他 Solve.* 程序集会被拒绝，加载失败，防止驱动绑定内核内部实现。</para>
/// <para>不可卸载（非 collectible）：驱动包只在启动时加载，更换驱动包需要重启程序。</para>
/// </remarks>
internal sealed class PackageLoadContext : AssemblyLoadContext
{
    private static readonly string SdkAssemblyName = typeof(IDriverPackage).Assembly.GetName().Name!;

    private readonly IReadOnlyDictionary<string, byte[]> _files;

    public PackageLoadContext(string packageId, IReadOnlyDictionary<string, byte[]> files)
        : base($"DeviceLibrary:{packageId}", isCollectible: false)
    {
        _files = files;
    }

    public Assembly LoadPackageAssembly(string fileName)
    {
        using var stream = new MemoryStream(_files[fileName], writable: false);
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || string.Equals(name, SdkAssemblyName, StringComparison.Ordinal))
            return null;

        if (name.StartsWith("Solve.", StringComparison.Ordinal))
        {
            throw new FileLoadException(
                $"驱动包只能依赖 {SdkAssemblyName}，不能引用内核程序集 {name}。", assemblyName.FullName);
        }

        // 包根目录或任意子目录中的同名 .dll；找不到时交给默认上下文（.NET 基础库）
        var match = _files.Keys.FirstOrDefault(
            path => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        return match is null ? null : LoadPackageAssembly(match);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        => throw new DllNotFoundException($"驱动包不允许加载本机库 {unmanagedDllName}：驱动只编解码报文，不需要本机代码。");
}
