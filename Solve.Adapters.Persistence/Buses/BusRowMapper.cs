using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;

namespace Solve.Adapters.Persistence.Buses;

/// <summary>
/// 领域描述 ⇄ 数据库行的显式转换。
/// </summary>
/// <remarks>
/// 时间统一存整毫秒（INTEGER），便于现场工程师直接读库；不足 1 ms 的精度在保存时拒绝，保证读写往返无损。
/// 枚举存名称文本（TEXT），不依赖数值。读取时把库内数据视为不可信输入：解析错误与领域校验错误一并收集。
/// </remarks>
internal static class BusRowMapper
{
    public static Result<BusRow> ToRow(BusDescriptor descriptor, DateTimeOffset now)
    {
        var errors = new List<Error>();
        var timestamp = now.UtcDateTime.ToString("O");

        var row = new BusRow
        {
            Id = descriptor.Id.Value.ToString("D"),
            Name = descriptor.Name,
            ConnectTimeoutMs = WholeMilliseconds(descriptor.Timeouts.Connect, "Timeouts.Connect", errors),
            ReadTimeoutMs = WholeMilliseconds(descriptor.Timeouts.Read, "Timeouts.Read", errors),
            WriteTimeoutMs = WholeMilliseconds(descriptor.Timeouts.Write, "Timeouts.Write", errors),
            ReconnectInitialDelayMs = WholeMilliseconds(descriptor.Reconnect.InitialDelay, "Reconnect.InitialDelay", errors),
            ReconnectMaxDelayMs = WholeMilliseconds(descriptor.Reconnect.MaxDelay, "Reconnect.MaxDelay", errors),
            ReconnectMultiplier = descriptor.Reconnect.Multiplier,
            ReconnectMaxAttempts = descriptor.Reconnect.MaxAttempts,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        switch (descriptor)
        {
            case SerialBusDescriptor serial:
                row.Kind = BusRow.KindSerial;
                row.PortName = serial.PortName;
                row.BaudRate = serial.BaudRate;
                row.DataBits = serial.DataBits;
                row.Parity = serial.Parity.ToString();
                row.StopBits = serial.StopBits.ToString();
                break;

            case TcpBusDescriptor tcp:
                row.Kind = BusRow.KindTcp;
                row.Host = tcp.Host;
                row.Port = tcp.Port;
                break;

            default:
                return Result<BusRow>.Failure(BusErrors.UnsupportedDescriptor(descriptor.GetType()));
        }

        return errors.Count == 0
            ? Result<BusRow>.Success(row)
            : Result<BusRow>.Failure(BusErrors.InvalidDescriptor(errors));
    }

    /// <summary>
    /// 行 → 描述。失败时返回该行的全部问题（解析错误 + 领域校验错误）。
    /// </summary>
    public static Result<BusDescriptor> ToDescriptor(BusRow row, out IReadOnlyList<Error> problems)
    {
        var errors = new List<Error>();

        if (!BusId.TryParse(row.Id, out var id))
            errors.Add(BusErrors.StorageCorruptRow("id", $"'{row.Id}' 不是有效的 GUID"));

        var timeouts = new BusTimeouts(
            Milliseconds(row.ConnectTimeoutMs, "connect_timeout_ms", errors),
            Milliseconds(row.ReadTimeoutMs, "read_timeout_ms", errors),
            Milliseconds(row.WriteTimeoutMs, "write_timeout_ms", errors));

        var reconnect = new ReconnectPolicy(
            Milliseconds(row.ReconnectInitialDelayMs, "reconnect_initial_delay_ms", errors),
            Milliseconds(row.ReconnectMaxDelayMs, "reconnect_max_delay_ms", errors),
            row.ReconnectMultiplier,
            ToInt32(row.ReconnectMaxAttempts, "reconnect_max_attempts", errors));

        BusDescriptor? descriptor = row.Kind switch
        {
            BusRow.KindSerial => new SerialBusDescriptor(id, row.Name, row.PortName!)
            {
                BaudRate = ToInt32(row.BaudRate, "baud_rate", errors) ?? 0,
                DataBits = ToInt32(row.DataBits, "data_bits", errors) ?? 0,
                Parity = ParseEnum<SerialParity>(row.Parity, "parity", errors),
                StopBits = ParseEnum<SerialStopBits>(row.StopBits, "stop_bits", errors),
                Timeouts = timeouts,
                Reconnect = reconnect,
            },
            BusRow.KindTcp => new TcpBusDescriptor(id, row.Name, row.Host!, ToInt32(row.Port, "port", errors) ?? 0)
            {
                Timeouts = timeouts,
                Reconnect = reconnect,
            },
            _ => null,
        };

        if (descriptor is null)
        {
            errors.Add(BusErrors.StorageCorruptRow("kind", $"未知总线类型 '{row.Kind}'"));
        }
        else if (errors.Count == 0)
        {
            // 解析无误后再做领域校验：库文件可能被手工修改成越界值
            errors.AddRange(descriptor.Validate());
        }

        problems = errors;
        return errors.Count == 0
            ? Result<BusDescriptor>.Success(descriptor!)
            : Result<BusDescriptor>.Failure(BusErrors.InvalidDescriptor(errors));
    }

    private static long WholeMilliseconds(TimeSpan value, string field, List<Error> errors)
    {
        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
            errors.Add(BusErrors.InvalidField(field, $"{field} 必须是整毫秒，当前为 {value}（存储精度为 1 ms）。"));

        return value.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static TimeSpan Milliseconds(long value, string column, List<Error> errors)
    {
        // TimeSpan 上限约 2.9 万年，int 范围（约 24.8 天）已远超任何合法超时，超出即视为损坏
        if (value is < int.MinValue or > int.MaxValue)
        {
            errors.Add(BusErrors.StorageCorruptRow(column, $"{value} ms 超出可表示范围"));
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMilliseconds(value);
    }

    private static int? ToInt32(long? value, string column, List<Error> errors)
    {
        if (value is null)
            return null;

        if (value is < int.MinValue or > int.MaxValue)
        {
            errors.Add(BusErrors.StorageCorruptRow(column, $"{value} 超出 32 位整数范围"));
            return null;
        }

        return (int)value.Value;
    }

    private static TEnum ParseEnum<TEnum>(string? text, string column, List<Error> errors)
        where TEnum : struct, Enum
    {
        // Enum.TryParse 也接受 "99"、"A, B" 这类文本，必须再用 IsDefined 把关
        if (Enum.TryParse<TEnum>(text, ignoreCase: false, out var value) && Enum.IsDefined(value)
            && string.Equals(value.ToString(), text, StringComparison.Ordinal))
        {
            return value;
        }

        errors.Add(BusErrors.StorageCorruptRow(column, $"'{text}' 不是有效的 {typeof(TEnum).Name}"));
        return default;
    }
}
