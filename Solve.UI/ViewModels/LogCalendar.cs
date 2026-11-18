using System.Globalization;

namespace Solve.UI.ViewModels;

/// <summary>
/// 月历中的一格。不属于所显示月份的格子只用来占位，界面上不显示，各标志都为 false。
/// </summary>
/// <param name="IsInMonth">属于所显示的月份</param>
/// <param name="HasLog">该日有日志文件</param>
/// <param name="IsSelected">正在查看的日期；即使该日的日志文件刚被清理也保持选中</param>
public sealed record LogCalendarDay(DateOnly Date, bool IsInMonth, bool HasLog, bool IsToday, bool IsSelected)
{
    public string DayText => Date.Day.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 可以点选：有日志，或者就是正在查看的那天（再点它不会重新读取）
    /// </summary>
    public bool CanSelect => IsInMonth && (HasLog || IsSelected);

    public string ToolTip
    {
        get
        {
            var date = IsToday ? $"{Date:yyyy-MM-dd}（今天）" : $"{Date:yyyy-MM-dd}";
            if (HasLog)
                return date;
            return IsSelected ? $"{date}：日志文件已被清理，界面中为之前读取的内容" : $"{date}：没有日志";
        }
    }
}

/// <summary>
/// 年份下拉的一项。按值相等：年份列表重建后下拉框仍能对上当前选中项
/// </summary>
public sealed record CalendarYear(int Value)
{
    public override string ToString() => $"{Value} 年";
}

/// <summary>
/// 月份下拉的一项
/// </summary>
public sealed record CalendarMonth(int Value)
{
    public override string ToString() => $"{Value} 月";
}

/// <summary>
/// 按月生成月历格子。纯函数，不做任何 I/O，不读系统时钟。
/// </summary>
public static class LogCalendar
{
    public const int Weeks = 6;

    /// <summary>
    /// 固定 6 周 42 格：任何月份都放得下（最多跨 6 周），翻月时面板高度不跳动
    /// </summary>
    public const int CellCount = Weeks * 7;

    /// <summary>
    /// 一周从周一开始（国内习惯）
    /// </summary>
    public const DayOfWeek FirstDayOfWeek = DayOfWeek.Monday;

    /// <summary>
    /// 月历支持的年份范围。日志文件名中的年份超出此范围（系统时钟错误、手工改名）时不在月历中显示，
    /// 免得年份下拉被一个离群值撑出上千项；同时保证首尾月份的占位格不越过 <see cref="DateOnly"/> 的取值范围
    /// </summary>
    public const int MinYear = 2000;

    public const int MaxYear = 2099;

    public static bool IsSupported(DateOnly date) => date.Year is >= MinYear and <= MaxYear;

    /// <summary>
    /// 某月的 42 个格子：从该月 1 日所在那一周的周一开始
    /// </summary>
    /// <param name="available">有日志的日期</param>
    /// <param name="selected">正在查看的日期；查看实时日志时为 null</param>
    public static IReadOnlyList<LogCalendarDay> BuildMonth(
        int year,
        int month,
        IReadOnlySet<DateOnly> available,
        DateOnly today,
        DateOnly? selected)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, MinYear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, MaxYear);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        ArgumentNullException.ThrowIfNull(available);

        var first = new DateOnly(year, month, 1);
        var leading = ((int)first.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        var start = first.AddDays(-leading);

        var cells = new LogCalendarDay[CellCount];
        for (var i = 0; i < cells.Length; i++)
        {
            var date = start.AddDays(i);
            var inMonth = date.Year == year && date.Month == month;
            cells[i] = inMonth
                ? new LogCalendarDay(date, true, available.Contains(date), date == today, date == selected)
                : new LogCalendarDay(date, false, false, false, false);
        }

        return cells;
    }
}
