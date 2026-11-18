using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Solve.Desktop.Composition;

/// <summary>
/// 桌面程序启动参数与文件位置
/// </summary>
/// <param name="HardwareRequested">命令行是否带 --hardware（实机模式的必要条件之一）</param>
/// <param name="DatabasePath">配置库文件</param>
/// <param name="LogDirectory">黑匣子日志目录</param>
/// <param name="LoggingSettingsPath">日志级别配置文件；为 null 时不读取，使用默认级别</param>
public sealed record DesktopOptions(
    bool HardwareRequested,
    string DatabasePath,
    string LogDirectory,
    string? LoggingSettingsPath = null)
{
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
            LoggingSettingsPath: Path.Combine(baseDirectory, LoggingSettingsFileName));
    }
}
