using System;
using System.Threading;
using Serilog.Core;
using Serilog.Events;

namespace Solve.Desktop.Logging;

/// <summary>
/// 可在运行中切换目录的日志文件输出。挂在日志管线的异步队列之后，<see cref="Emit"/> 只在后台写盘线程上调用。
/// </summary>
/// <remarks>
/// <para>根日志器已交给 Avalonia 和各模块的日志器工厂，切换目录不能重建根日志器，只替换这一层里的文件输出。</para>
/// <para>切换瞬间异步队列中尚未写出的几条会写进新目录：两个目录合起来不丢不重，分界处可能相差几条。</para>
/// </remarks>
internal sealed class SwitchableFileSink : ILogEventSink, IDisposable
{
    private readonly Func<string, Logger> _createFileLogger;
    private readonly Lock _gate = new();
    private Logger _current;
    private bool _disposed;

    /// <param name="directory">初始目录</param>
    /// <param name="createFileLogger">为指定目录创建只含文件输出的日志器；创建时不打开文件，首次写入才打开</param>
    public SwitchableFileSink(string directory, Func<string, Logger> createFileLogger)
    {
        _createFileLogger = createFileLogger ?? throw new ArgumentNullException(nameof(createFileLogger));
        _current = createFileLogger(directory);
    }

    public void Emit(LogEvent logEvent)
    {
        // 锁只与切换竞争，平时无人争用；保证不会写进正在关闭的旧文件
        lock (_gate)
        {
            if (!_disposed)
                _current.Write(logEvent);
        }
    }

    /// <summary>
    /// 之后的日志写入 <paramref name="directory"/>，旧文件刷写后关闭。
    /// </summary>
    public void SwitchTo(string directory)
    {
        var next = _createFileLogger(directory);
        Logger previous;
        lock (_gate)
        {
            if (_disposed)
            {
                next.Dispose();
                throw new ObjectDisposedException(nameof(SwitchableFileSink));
            }

            previous = _current;
            _current = next;
        }

        // 锁外关闭旧文件：交换之后 Emit 不会再用到它，刷写也不阻塞后台写盘线程
        previous.Dispose();
    }

    /// <summary>
    /// 刷写并关闭当前文件。可重复调用：根日志器释放时会连带释放本输出，管线也会再显式调用一次。
    /// </summary>
    public void Dispose()
    {
        Logger current;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            current = _current;
        }

        current.Dispose();
    }
}
