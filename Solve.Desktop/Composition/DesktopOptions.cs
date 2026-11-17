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
public sealed record DesktopOptions(bool HardwareRequested, string DatabasePath, string LogDirectory)
{
    public const string HardwareArgument = "--hardware";

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
            LogDirectory: Path.Combine(baseDirectory, "logs"));
    }
}
