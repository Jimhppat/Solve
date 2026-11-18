using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Logging;

namespace Solve.Desktop.Logging;

/// <summary>
/// 把日志文件的文本逐行解析回 <see cref="LogEntry"/>。有状态：一条日志可能跨多行（异常堆栈），
/// 读到下一条的首行或调用 <see cref="Flush"/> 时才产出上一条。
/// </summary>
/// <remarks>
/// <para>首行格式对应 <see cref="LoggingPipeline.OutputTemplate"/>：
/// 「2026-11-18 14:53:23.755 +09:00 [INF] [12] Solve.Xxx: 消息」，两处修改必须同步（有往返测试把关）。
/// 升级前的文件没有「[线程号]」，同样识别。</para>
/// <para>首行之后、下一条首行之前的行归为异常信息。消息本身含换行时，第二行起也会被归入异常信息，
/// 内容不丢，只是显示位置不同。</para>
/// <para>第一条首行之前的内容（文件被截断等）无法归属，直接跳过。</para>
/// </remarks>
internal sealed class LogTextParser
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    private static readonly Regex Header = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<level>VRB|DBG|INF|WRN|ERR|FTL)\] (?:\[\d*\] )?(?<source>[^\s:]*): (?<message>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private Pending? _pending;

    /// <summary>
    /// 送入一行（不含换行符）。读到新一条的首行时返回已完整的上一条，否则返回 null。
    /// </summary>
    public LogEntry? Feed(string line)
    {
        var match = Header.Match(line);
        if (!match.Success)
        {
            _pending?.AppendContinuation(line);
            return null;
        }

        var completed = Flush();
        if (DateTimeOffset.TryParseExact(match.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
        {
            _pending = new Pending(timestamp, MapLevel(match.Groups["level"].Value),
                match.Groups["source"].Value, match.Groups["message"].Value);
        }

        return completed;
    }

    /// <summary>
    /// 产出尚未完成的最后一条（文件结束时调用）。之后可以继续送入下一个文件的内容。
    /// </summary>
    public LogEntry? Flush()
    {
        var entry = _pending?.ToEntry();
        _pending = null;
        return entry;
    }

    private static LogLevel MapLevel(string level) => level switch
    {
        "VRB" => LogLevel.Trace,
        "DBG" => LogLevel.Debug,
        "INF" => LogLevel.Information,
        "WRN" => LogLevel.Warning,
        "ERR" => LogLevel.Error,
        _ => LogLevel.Critical,
    };

    private sealed class Pending(DateTimeOffset timestamp, LogLevel level, string source, string message)
    {
        private StringBuilder? _continuation;

        public void AppendContinuation(string line)
        {
            _continuation ??= new StringBuilder();
            if (_continuation.Length > 0)
                _continuation.AppendLine();
            _continuation.Append(line);
        }

        public LogEntry ToEntry()
        {
            // 异常块末尾有模板的空行，去掉；只有空白时视为没有异常
            var exception = _continuation?.ToString().TrimEnd();
            return new LogEntry(timestamp, level, source, message, string.IsNullOrEmpty(exception) ? null : exception);
        }
    }
}
