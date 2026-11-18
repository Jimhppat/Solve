namespace Solve.UI.ViewModels;

/// <summary>
/// 运行日志页的日期月历：选年、月，按月列出每一天；有日志的日期可点，没有日志的置灰。
/// </summary>
/// <remarks>
/// <para>只负责「显示哪个月、哪些天可选」，不读磁盘：有日志的日期与正在查看的日期都由 <see cref="RuntimeLogViewModel"/> 告知。</para>
/// <para>可翻到的年份从最早有日志的年份到今年（也包含正在查看的日期所在年份），超出时自动收拢到边界。</para>
/// <para>只能在 UI 线程上使用。</para>
/// </remarks>
public sealed class LogCalendarViewModel : ViewModelBase
{
    public static IReadOnlyList<CalendarMonth> AllMonths { get; } =
        Enumerable.Range(1, 12).Select(m => new CalendarMonth(m)).ToArray();

    private HashSet<DateOnly> _available = [];
    private DateOnly _today;
    private DateOnly? _selected;
    private int _firstYear;
    private int _lastYear;
    private int _year;
    private int _month;
    private IReadOnlyList<CalendarYear> _years = [];
    private IReadOnlyList<LogCalendarDay> _days = [];

    public LogCalendarViewModel(DateOnly today)
    {
        _today = today;
        _year = Math.Clamp(today.Year, LogCalendar.MinYear, LogCalendar.MaxYear);
        _month = today.Month;

        PreviousMonthCommand = new RelayCommand(() => ShowMonth(_year, _month - 1), () => CanGoPrevious);
        NextMonthCommand = new RelayCommand(() => ShowMonth(_year, _month + 1), () => CanGoNext);

        Update();
    }

    /// <summary>
    /// 年份下拉的选项，从早到晚
    /// </summary>
    public IReadOnlyList<CalendarYear> Years
    {
        get => _years;
        private set => SetProperty(ref _years, value);
    }

    public IReadOnlyList<CalendarMonth> Months => AllMonths;

    /// <summary>
    /// 下拉框更换数据源时可能短暂写入 null，忽略
    /// </summary>
    public CalendarYear? SelectedYear
    {
        get => new(_year);
        set
        {
            if (value is not null)
                ShowMonth(value.Value, _month);
        }
    }

    public CalendarMonth? SelectedMonth
    {
        get => AllMonths[_month - 1];
        set
        {
            if (value is not null)
                ShowMonth(_year, value.Value);
        }
    }

    public int DisplayYear => _year;

    public int DisplayMonth => _month;

    /// <summary>
    /// 所显示月份的 42 个格子（见 <see cref="LogCalendar.BuildMonth"/>）
    /// </summary>
    public IReadOnlyList<LogCalendarDay> Days
    {
        get => _days;
        private set => SetProperty(ref _days, value);
    }

    public bool TodayHasLog => _available.Contains(_today);

    public RelayCommand PreviousMonthCommand { get; }

    public RelayCommand NextMonthCommand { get; }

    private bool CanGoPrevious => MonthIndex(_year, _month) > MonthIndex(_firstYear, 1);

    private bool CanGoNext => MonthIndex(_year, _month) < MonthIndex(_lastYear, 12);

    public bool HasLog(DateOnly date) => _available.Contains(date);

    /// <summary>
    /// 翻到某年某月。月份越界时进位或借位（0 月即上一年 12 月），超出可翻范围时收拢到边界
    /// </summary>
    public void ShowMonth(int year, int month)
    {
        var index = Math.Clamp(MonthIndex(year, month), MonthIndex(_firstYear, 1), MonthIndex(_lastYear, 12));
        var newYear = index / 12;
        var newMonth = index % 12 + 1;
        if (newYear == _year && newMonth == _month)
        {
            // 下拉框可能已显示为被收拢前的值：重新通知一次，让它回到实际显示的年月
            NotifyDisplayMonth();
            return;
        }

        _year = newYear;
        _month = newMonth;
        Rebuild();
    }

    public void ShowMonthOf(DateOnly date) => ShowMonth(date.Year, date.Month);

    /// <summary>
    /// 更新有日志的日期与「今天」。超出支持范围的日期忽略（见 <see cref="LogCalendar.MinYear"/>）
    /// </summary>
    public void SetAvailableDates(IEnumerable<DateOnly> dates, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(dates);
        _available = dates.Where(LogCalendar.IsSupported).ToHashSet();
        _today = today;
        Update();
    }

    /// <summary>
    /// 正在查看的日期；查看实时日志时为 null
    /// </summary>
    public void SetSelected(DateOnly? selected)
    {
        if (_selected == selected)
            return;

        _selected = selected;
        Update();
    }

    // 年份范围可能因日期列表、今天或选中日期变化而改变：先定范围，再把所显示的年月收拢进来，最后重建格子
    private void Update()
    {
        var years = _available.Select(d => d.Year).Append(_today.Year);
        if (_selected is { } selected)
            years = years.Append(selected.Year);

        var yearList = years.ToList();
        _firstYear = Math.Clamp(yearList.Min(), LogCalendar.MinYear, LogCalendar.MaxYear);
        _lastYear = Math.Clamp(yearList.Max(), LogCalendar.MinYear, LogCalendar.MaxYear);

        var count = _lastYear - _firstYear + 1;
        if (_years.Count != count || _years[0].Value != _firstYear)
        {
            // 先换列表再通知选中项：下拉框换数据源时清掉的选中项随后由 SelectedYear 的通知恢复
            Years = Enumerable.Range(_firstYear, count).Select(y => new CalendarYear(y)).ToArray();
        }

        var index = Math.Clamp(MonthIndex(_year, _month), MonthIndex(_firstYear, 1), MonthIndex(_lastYear, 12));
        _year = index / 12;
        _month = index % 12 + 1;
        Rebuild();
    }

    private void Rebuild()
    {
        Days = LogCalendar.BuildMonth(_year, _month, _available, _today, _selected);
        NotifyDisplayMonth();
        OnPropertyChanged(nameof(TodayHasLog));
    }

    private void NotifyDisplayMonth()
    {
        OnPropertyChanged(nameof(SelectedYear));
        OnPropertyChanged(nameof(SelectedMonth));
        OnPropertyChanged(nameof(DisplayYear));
        OnPropertyChanged(nameof(DisplayMonth));
        PreviousMonthCommand.RaiseCanExecuteChanged();
        NextMonthCommand.RaiseCanExecuteChanged();
    }

    private static int MonthIndex(int year, int month) => year * 12 + (month - 1);
}
