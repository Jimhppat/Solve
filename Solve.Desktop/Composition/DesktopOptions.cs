using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Solve.Desktop.Logging;

namespace Solve.Desktop.Composition;

/// <summary>
/// 桌面程序启动参数与文件位置
/// </summary>
/// <param name="HardwareRequested">命令行是否带 --hardware（实机模式的必要条件之一）</param>
/// <param name="DatabasePath">配置库文件</param>
/// <param name="LogDirectory">默认的黑匣子日志目录；设置页可改到其他目录，与保存天数等一起保存在 <paramref name="LogLocationPath"/></param>
/// <param name="LoggingSettingsPath">日志级别配置文件；为 null 时不读取，使用默认级别</param>
/// <param name="LogLocationPath">日志设置文件（目录、保存天数、单个文件上限）；为 null 时始终使用默认设置，设置页不能保存</param>
/// <param name="DeviceLibraryDirectory">默认的驱动库目录（第三方驱动包）；设置页可改到其他目录（保存在配置库，重启后生效）。为 null 时不扫描，只有程序内置驱动</param>
public sealed record DesktopOptions(
    bool HardwareRequested,
    string DatabasePath,
    string LogDirectory,
    string? LoggingSettingsPath = null,
    string? LogLocationPath = null,
    string? DeviceLibraryDirectory = null)
{
    public const string DeviceLibraryDirectoryName = "DeviceLibrary";

    /// <summary>
    /// 源码目录中驱动库的文件夹名：与解决方案文件同级，驱动源码与编译后自动产出的驱动包都在这里
    /// </summary>
    public const string SourceDeviceLibraryDirectoryName = "Devicelibrary";

    public const string SolutionFileName = "Solve.slnx";

    public const string HardwareArgument = "--hardware";

    public const string LoggingSettingsFileName = "appsettings.json";

    /// <summary>
    /// 从命令行与程序目录构建。--hardware 区分大小写、必须完全一致，避免拼写近似的参数被误认为授权。
    /// </summary>
    public static DesktopOptions FromArgs(IReadOnlyList<string> args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return new DesktopOptions(
            HardwareRequested: args.Any(a => string.Equals(a, HardwareArgument, StringComparison.Ordinal)),
            DatabasePath: Path.Combine(baseDirectory, "config", "solve.db"),
            LogDirectory: Path.Combine(baseDirectory, "logs"),
            LoggingSettingsPath: Path.Combine(baseDirectory, LoggingSettingsFileName),
            LogLocationPath: Path.Combine(baseDirectory, "config", LogSettingsStore.FileName),
            DeviceLibraryDirectory: DefaultDeviceLibraryDirectory(baseDirectory));
    }

    /// <summary>
    /// 默认驱动库目录：程序从源码目录运行（向上能找到 Solve.slnx，且它旁边有 Devicelibrary）时，用源码目录里的驱动库，
    /// 驱动项目编译后自动产出的驱动包就放在那里；部署后的程序上方没有解决方案文件，用程序目录下的 DeviceLibrary。
    /// </summary>
    public static string DefaultDeviceLibraryDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                continue;

            var source = Path.Combine(directory.FullName, SourceDeviceLibraryDirectoryName);
            if (Directory.Exists(source))
                return source;

            break;
        }

        return Path.Combine(baseDirectory, DeviceLibraryDirectoryName);
    }
}
