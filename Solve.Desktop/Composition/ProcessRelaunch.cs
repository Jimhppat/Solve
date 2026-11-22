using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Solve.Desktop.Composition;

/// <summary>
/// 以与当前进程相同的可执行文件、命令行参数和工作目录启动新实例（模式切换后的受控重启）
/// </summary>
public static class ProcessRelaunch
{
    /// <summary>
    /// 按当前进程构造启动信息；拿不到可执行文件路径时返回 null
    /// </summary>
    /// <param name="args">Main 收到的参数（不含程序名），原样传给新进程，--hardware 许可随之保留</param>
    public static ProcessStartInfo? CreateStartInfo(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            return null;

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        // 经 dotnet 宿主运行（dotnet Solve.Desktop.dll）时，进程路径是 dotnet，程序集路径要作为第一个参数传回去
        if (IsDotnetHost(processPath) && Environment.GetCommandLineArgs().FirstOrDefault() is { Length: > 0 } entryAssembly)
            startInfo.ArgumentList.Add(entryAssembly);

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private static bool IsDotnetHost(string processPath)
        => string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
}
