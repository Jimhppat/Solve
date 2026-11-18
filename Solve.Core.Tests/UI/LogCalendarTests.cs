using Solve.UI.ViewModels;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 运行日志页的日期月历：格子生成（纯函数）与年月切换。「今天」由测试指定，不依赖系统时钟。
/// </summary>
public sealed class LogCalendarTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static readonly HashSet<DateOnly> NoLogs = [];

    private static List<LogCalendarDay> InMonth(IReadOnlyList<LogCalendarDay> cells) => cells.Where(c => c.IsInMonth).ToList();

    // ==================== 格子 ====================

    [Fact]
    public void Month_AlwaysHas42Cells_StartingOnMonday()
    {
        var cells = LogCalendar.BuildMonth(2026, 11, NoLogs, Today, null);

        Assert.Equal(LogCalendar.CellCount, cells.Count);
        Assert.Equal(DayOfWeek.Monday, cells[0].Date.DayOfWeek);
        Assert.All(cells.Zip(cells.Skip(1)), pair => Assert.Equal(pair.First.Date.AddDays(1), pair.Second.Date));
    }

    [Fact]
    public void MonthStartingOnSunday_HasSixLeadingPlaceholders()
    {
        // 2026-11-01 是周日：前面空出周一到周六
        var cells = LogCalendar.BuildMonth(2026, 11, NoLogs, Today, null);

        Assert.All(cells.Take(6), c => Assert.False(c.IsInMonth));
        Assert.Equal(new DateOnly(2026, 11, 1), cells[6].Date);
        Assert.True(cells[6].IsInMonth);
        Assert.Equal(30, InMonth(cells).Count);
        Assert.Equal(new DateOnly(2026, 11, 30), InMonth(cells)[^1].Date);
    }

    [Fact]
    public void MonthStartingOnMonday_HasNoLeadingPlaceholder()
    {
        // 2027-02-01 是周一，平年 28 天正好四整周
        var cells = LogCalendar.BuildMonth(2027, 2, NoLogs, Today, null);

        Assert.Equal(new DateOnly(2027, 2, 1), cells[0].Date);
        Assert.Equal(28, InMonth(cells).Count);
        Assert.All(cells.Skip(28), c => Assert.False(c.IsInMonth));
    }

    [Fact]
    public void LeapFebruary_Has29Days()
    {
        Assert.Equal(29, InMonth(LogCalendar.BuildMonth(2028, 2, NoLogs, Today, null)).Count);
    }

    [Fact]
    public void Flags_FollowAvailableDates_TodayAndSelection()
    {
        var available = new HashSet<DateOnly> { new(2026, 11, 16), new(2026, 11, 18) };

        var cells = InMonth(LogCalendar.BuildMonth(2026, 11, available, Today, new DateOnly(2026, 11, 16)));
        LogCalendarDay Day(int day) => cells[day - 1];

        Assert.True(Day(16).HasLog);
        Assert.True(Day(16).IsSelected);
        Assert.True(Day(16).CanSelect);
        Assert.Equal("16", Day(16).DayText);

        Assert.True(Day(18).IsToday);
        Assert.Equal("2026-11-18（今天）", Day(18).ToolTip);

        Assert.False(Day(17).HasLog);
        Assert.False(Day(17).CanSelect);
        Assert.Equal("2026-11-17：没有日志", Day(17).ToolTip);

        Assert.Single(cells, c => c.IsSelected);
        Assert.Single(cells, c => c.IsToday);
    }

    [Fact]
    public void Placeholders_CarryNoFlags_EvenForDatesWithLogs()
    {
        // 10-31 有日志，但它只是 11 月月历的占位格：不显示、不可选
        var available = new HashSet<DateOnly> { new(2026, 10, 31) };

        var cells = LogCalendar.BuildMonth(2026, 11, available, new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 31));
        var placeholder = cells.Single(c => c.Date == new DateOnly(2026, 10, 31));

        Assert.False(placeholder.IsInMonth);
        Assert.False(placeholder.HasLog);
        Assert.False(placeholder.IsToday);
        Assert.False(placeholder.IsSelected);
        Assert.False(placeholder.CanSelect);
    }

    [Theory]
    [InlineData(1999, 12)]
    [InlineData(2100, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void OutOfRangeMonth_Throws(int year, int month)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogCalendar.BuildMonth(year, month, NoLogs, Today, null));
    }

    [Fact]
    public void SupportedRange_EdgesDoNotOverflow()
    {
        Assert.Equal(31, InMonth(LogCalendar.BuildMonth(LogCalendar.MinYear, 1, NoLogs, Today, null)).Count);
        Assert.Equal(31, InMonth(LogCalendar.BuildMonth(LogCalendar.MaxYear, 12, NoLogs, Today, null)).Count);
    }

    // ==================== 年月切换 ====================

    [Fact]
    public void StartsOnThisMonth_WithOnlyThisYear()
    {
        var calendar = new LogCalendarViewModel(Today);

        Assert.Equal((2026, 11), (calendar.DisplayYear, calendar.DisplayMonth));
        Assert.Equal([new CalendarYear(2026)], calendar.Years);
        Assert.Equal(new CalendarYear(2026), calendar.SelectedYear);
        Assert.Equal(new CalendarMonth(11), calendar.SelectedMonth);
        Assert.Equal(12, calendar.Months.Count);
        Assert.Equal("11 月没有日志", calendar.MonthSummary);
    }

    [Fact]
    public void Years_SpanFromEarliestLogToThisYear()
    {
        var calendar = new LogCalendarViewModel(new DateOnly(2027, 1, 5));

        calendar.SetAvailableDates([new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 3)], new DateOnly(2027, 1, 5));

        Assert.Equal([new CalendarYear(2026), new CalendarYear(2027)], calendar.Years);
        Assert.Equal("2026 年", calendar.Years[0].ToString());
        Assert.Equal("1 月共 1 天有日志", calendar.MonthSummary);
    }

    [Fact]
    public void PreviousMonth_CrossesIntoLastYear()
    {
        var today = new DateOnly(2027, 1, 5);
        var calendar = new LogCalendarViewModel(today);
        calendar.SetAvailableDates([new DateOnly(2026, 12, 20)], today);

        calendar.PreviousMonthCommand.Execute(null);

        Assert.Equal((2026, 12), (calendar.DisplayYear, calendar.DisplayMonth));
        Assert.True(calendar.Days.Single(d => d.Date == new DateOnly(2026, 12, 20)).CanSelect);
    }

    [Fact]
    public void Navigation_IsBoundedByTheYearRange()
    {
        var calendar = new LogCalendarViewModel(Today);
        calendar.SetAvailableDates([new DateOnly(2026, 11, 16)], Today);

        calendar.ShowMonth(2026, 1);
        Assert.False(calendar.PreviousMonthCommand.CanExecute(null));
        Assert.True(calendar.NextMonthCommand.CanExecute(null));

        calendar.ShowMonth(2026, 12);
        Assert.False(calendar.NextMonthCommand.CanExecute(null));

        // 越界时收拢到边界，不抛出
        calendar.ShowMonth(2030, 6);
        Assert.Equal((2026, 12), (calendar.DisplayYear, calendar.DisplayMonth));
        calendar.ShowMonth(2026, 0);
        Assert.Equal((2026, 1), (calendar.DisplayYear, calendar.DisplayMonth));
    }

    [Fact]
    public void YearAndMonthDropDowns_ChangeTheDisplayedMonth_AndIgnoreNull()
    {
        var today = new DateOnly(2027, 1, 5);
        var calendar = new LogCalendarViewModel(today);
        calendar.SetAvailableDates([new DateOnly(2026, 3, 1)], today);

        calendar.SelectedYear = new CalendarYear(2026);
        calendar.SelectedMonth = new CalendarMonth(3);
        Assert.Equal((2026, 3), (calendar.DisplayYear, calendar.DisplayMonth));

        calendar.SelectedYear = null;
        calendar.SelectedMonth = null;
        Assert.Equal((2026, 3), (calendar.DisplayYear, calendar.DisplayMonth));
    }

    [Fact]
    public void Rebuilding_RaisesDaysAndSelection_SoTheDropDownsFollow()
    {
        var calendar = new LogCalendarViewModel(Today);
        var changed = new List<string?>();
        calendar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        calendar.ShowMonth(2026, 10);

        Assert.Contains(nameof(LogCalendarViewModel.Days), changed);
        Assert.Contains(nameof(LogCalendarViewModel.SelectedYear), changed);
        Assert.Contains(nameof(LogCalendarViewModel.SelectedMonth), changed);
        Assert.Contains(nameof(LogCalendarViewModel.MonthSummary), changed);
    }

    [Fact]
    public void ShrinkingRange_PullsTheDisplayedMonthBackIn()
    {
        var today = new DateOnly(2027, 1, 5);
        var calendar = new LogCalendarViewModel(today);
        calendar.SetAvailableDates([new DateOnly(2026, 12, 20)], today);
        calendar.ShowMonth(2026, 12);

        // 2026 年的日志都被清理了
        calendar.SetAvailableDates([new DateOnly(2027, 1, 3)], today);

        Assert.Equal([new CalendarYear(2027)], calendar.Years);
        Assert.Equal((2027, 1), (calendar.DisplayYear, calendar.DisplayMonth));
    }

    [Fact]
    public void SelectedDate_KeepsItsYearInRange_EvenAfterItsLogIsGone()
    {
        var today = new DateOnly(2027, 1, 5);
        var calendar = new LogCalendarViewModel(today);
        calendar.SetSelected(new DateOnly(2026, 12, 20));

        calendar.SetAvailableDates([], today);
        calendar.ShowMonthOf(new DateOnly(2026, 12, 20));

        Assert.Equal([new CalendarYear(2026), new CalendarYear(2027)], calendar.Years);
        var selected = calendar.Days.Single(d => d.Date == new DateOnly(2026, 12, 20));
        Assert.True(selected.IsSelected);
        Assert.True(selected.CanSelect);
    }

    [Fact]
    public void DatesOutsideTheSupportedYears_AreIgnored()
    {
        var calendar = new LogCalendarViewModel(Today);

        // 系统时钟错误或手工改名产生的离群日期：不撑大年份下拉
        calendar.SetAvailableDates([new DateOnly(1, 1, 1), new DateOnly(9999, 12, 31), new DateOnly(2026, 11, 16)], Today);

        Assert.Equal([new CalendarYear(2026)], calendar.Years);
        Assert.False(calendar.HasLog(new DateOnly(1, 1, 1)));
        Assert.True(calendar.HasLog(new DateOnly(2026, 11, 16)));
    }

    [Fact]
    public void TodayHasLog_FollowsAvailableDates_AndMidnight()
    {
        var calendar = new LogCalendarViewModel(Today);
        calendar.SetAvailableDates([Today], Today);
        Assert.True(calendar.TodayHasLog);

        // 跨午夜：新的一天还没写出日志文件
        var tomorrow = Today.AddDays(1);
        calendar.SetAvailableDates([Today], tomorrow);
        Assert.False(calendar.TodayHasLog);
        calendar.ShowMonthOf(tomorrow);
        Assert.True(calendar.Days.Single(d => d.Date == tomorrow).IsToday);
        Assert.False(calendar.Days.Single(d => d.Date == Today).IsToday);
    }
}
