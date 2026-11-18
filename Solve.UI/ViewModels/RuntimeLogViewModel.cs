using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia.Threading;
using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.UI.ViewModels;

/// <summary>
/// 日志来源：实时日志，或某一天的历史日志
/// </summary>
public sealed record LogSourceOption(string Label, DateOnly? Date)
{
    public static readonly LogSourceOption Live = new("实时", null);

    public static LogSourceOption ForDate(DateOnly date, DateOnly today)
        => new(date == today ? $"{date:yyyy-MM-dd}（今天）" : $"{date:yyyy-MM-dd}", date);

    public override string ToString() => Label;
}

/// <summary>
/// 运行日志列表：订阅实时日志，按级别（五类，可多选）与关键字筛选；接入历史日志后还可按日期查看与导出。
/// 运行日志页与主控台的最近日志控件各用一个实例，各自订阅、互不影响。
/// </summary>
/// <remarks>
/// <para>只是一个窗口，完整记录以日志文件为准：实时最多保留最近 <see cref="Capacity"/> 条，清空也只清界面。</para>
/// <para>新日志在后台按批取出、每 <see cref="BatchInterval"/> 最多向 UI 线程投递一次，日志密集时界面刷新频率不随之上升。</para>
/// <para>查看历史期间实时日志照常在后台累积，切回「实时」时不缺失。历史读取在后台进行，切换来源会取消上一次读取。</para>
/// <para>除 <see cref="Attach"/> 中启动的后台读取循环外，所有成员都只能在 UI 线程上使用；
/// 读取与导出的结果回到 UI 线程后再更新界面。</para>
/// </remarks>
public sealed class RuntimeLogViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 运行日志页默认保留的条数
    /// </summary>
    public const int DefaultCapacity = 2_000;

    /// <summary>
    /// 历史日志最多显示的条数：一天的日志可能很多，只取最后这些条，超出时提示
    /// </summary>
    public const int HistoryCapacity = 10_000;

    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 读取、导出历史日志的超时：磁盘异常（网络盘断开等）时不让界面一直停在「正在读取」
    /// </summary>
    public static readonly TimeSpan ArchiveTimeout = TimeSpan.FromSeconds(30);

    private readonly LogBuffer _live;

    // 正在查看的历史日志；为 null 表示显示实时日志
    private LogBuffer? _history;

    // 按 LogCategory 下标：是否显示。筛选在热路径上，用数组而不是逐项查找
    private readonly bool[] _shown = new bool[LogCategories.All.Count];

    private ObservableCollection<LogEntryItem> _entries = [];
    private EventSubscription<LogEntry>? _subscription;
    private ILogArchive? _archive;
    private CancellationTokenSource? _loadCts;
    private LogSourceOption _selectedSource = LogSourceOption.Live;
    private bool _isLoading;
    private bool _isExporting;
    private string? _statusText;
    private string _searchText = string.Empty;
    private bool _autoScroll = true;
    private string? _logDirectory;
    private int _unseenCount;

    /// <summary>
    /// 运行日志页（也供 XAML 设计器使用）：保留 <see cref="DefaultCapacity"/> 条，显示全部五类
    /// </summary>
    public RuntimeLogViewModel() : this(DefaultCapacity, LogCategories.All)
    {
    }

    /// <param name="capacity">实时日志保留的最多条数，超出时丢弃最旧的</param>
    /// <param name="shownCategories">初始显示的级别</param>
    public RuntimeLogViewModel(int capacity, IEnumerable<LogCategory> shownCategories)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(shownCategories);
        _live = new LogBuffer(capacity);

        foreach (var category in shownCategories)
            _shown[(int)category] = true;

        LevelFilters = LogCategories.All
            .Select(c => new LogLevelFilterOption(c, _shown[(int)c], OnLevelFilterChangedByUser))
            .ToArray();

        ClearCommand = new RelayCommand(Clear, () => IsLive);
        JumpToLatestCommand = new RelayCommand(() => AutoScroll = true);
        ShowAllLevelsCommand = new RelayCommand(() => SetShownCategories(LogCategories.All));
        ShowProblemsOnlyCommand = new RelayCommand(() => SetShownCategories(LogCategories.Problems));
    }

    /// <summary>
    /// 实时日志保留的最多条数，超出时丢弃最旧的
    /// </summary>
    public int Capacity => _live.Capacity;

    public string PageTitle => "运行日志";

    /// <summary>
    /// 五类级别的勾选项，按严重程度从低到高
    /// </summary>
    public IReadOnlyList<LogLevelFilterOption> LevelFilters { get; }

    /// <summary>
    /// 级别筛选按钮上的简短说明：全部 / 无 / 单个级别名 / 「警告+」（从某级到致命连续勾选）/ 「N 级」
    /// </summary>
    public string LevelFilterText
    {
        get
        {
            var selected = SelectedCategories();
            if (selected.Count == LogCategories.All.Count)
                return "全部";
            if (selected.Count == 0)
                return "无";
            if (selected.Count == 1)
                return selected[0].DisplayName();

            // 从某一级一直勾到致命，就是以前的「某级及以上」，保留这种直观写法
            var lowest = (int)selected[0];
            return selected.Count == LogCategories.All.Count - lowest
                ? $"{selected[0].DisplayName()}+"
                : $"{selected.Count} 级";
        }
    }

    /// <summary>
    /// 级别筛选按钮的悬停说明，列出全部已选级别
    /// </summary>
    public string LevelFilterDetail
    {
        get
        {
            var selected = SelectedCategories();
            if (selected.Count == LogCategories.All.Count)
                return "显示全部级别";
            return selected.Count == 0
                ? "未选择任何级别"
                : "显示：" + string.Join("、", selected.Select(c => c.DisplayName()));
        }
    }

    /// <summary>
    /// 筛选后显示的日志，按时间顺序
    /// </summary>
    public ObservableCollection<LogEntryItem> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
                Refilter();
        }
    }

    /// <summary>
    /// 有新日志时是否自动滚动到最后一条。关闭后列表停在当前位置，新日志照常追加并计入 <see cref="UnseenCount"/>
    /// </summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            // 重新开启即视为已看到最新：视图随之滚到底部
            if (SetProperty(ref _autoScroll, value) && value)
                ResetUnseen();
        }
    }

    /// <summary>
    /// 自动滚动关闭期间新增、且符合当前筛选的条数；不超过当前显示的条数
    /// </summary>
    public int UnseenCount => _unseenCount;

    public bool HasUnseen => _unseenCount > 0;

    public string UnseenText => _unseenCount > 99 ? "99+ 条新日志" : $"{_unseenCount} 条新日志";

    /// <summary>
    /// 日志文件所在目录；未接入日志源（如设计器）时为 null
    /// </summary>
    public string? LogDirectory
    {
        get => _logDirectory;
        private set
        {
            if (SetProperty(ref _logDirectory, value))
                OnPropertyChanged(nameof(HasLogDirectory));
        }
    }

    public bool HasLogDirectory => _logDirectory is not null;

    // ==================== 来源：实时 / 历史 ====================

    /// <summary>
    /// 是否接入了历史日志（按日期查看与导出）。主控台的最近日志只看实时，不接入
    /// </summary>
    public bool CanBrowseHistory => _archive is not null;

    /// <summary>
    /// 可选来源：「实时」在最前，其后为有日志的日期（从新到旧）
    /// </summary>
    public ObservableCollection<LogSourceOption> Sources { get; } = [LogSourceOption.Live];

    public LogSourceOption SelectedSource
    {
        get => _selectedSource;
        set
        {
            // 下拉框更换数据源时可能短暂写入 null；选同一项不重新读取
            if (value is null || Equals(value, _selectedSource))
                return;

            _ = SelectSourceAsync(value);
        }
    }

    public bool IsLive => _selectedSource.Date is null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// 读取、导出的进度与结果；没有可提示的内容时为 null
    /// </summary>
    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _statusText is not null;

    /// <summary>
    /// 导出的日期：查看历史时为所选日期，实时时为今天
    /// </summary>
    public DateOnly ExportDate => _selectedSource.Date ?? DateOnly.FromDateTime(DateTime.Now);

    public string ExportFileName => _archive?.SuggestExportFileName(ExportDate) ?? string.Empty;

    public string ExportTip => $"导出 {ExportDate:yyyy-MM-dd} 的日志文件（同日分卷合并为一个文件）";

    public bool CanExport => _archive is not null && !_isExporting;

    // ==================== 统计 ====================

    public int TotalCount => Current.Items.Count;

    public int WarningCount => Current.Counts[(int)LogCategory.Warning];

    /// <summary>
    /// 错误与致命合计
    /// </summary>
    public int ErrorCount => Current.Counts[(int)LogCategory.Error] + Current.Counts[(int)LogCategory.Critical];

    public string SummaryText
    {
        get
        {
            var summary = $"共 {TotalCount} 条 · 警告 {WarningCount} · 错误 {ErrorCount}";
            return IsFiltered ? $"{summary} · 筛选出 {Entries.Count} 条" : summary;
        }
    }

    public bool IsEmpty => Entries.Count == 0;

    public string EmptyText
    {
        get
        {
            if (!_shown.Contains(true))
                return "未选择任何级别";
            if (_isLoading)
                return "正在读取日志…";
            if (Current.Items.Count == 0)
                return IsLive ? "暂无日志" : "该日没有可显示的日志";
            return "没有符合筛选条件的日志";
        }
    }

    public RelayCommand ClearCommand { get; }

    /// <summary>
    /// 回到最新一条并恢复自动滚动
    /// </summary>
    public RelayCommand JumpToLatestCommand { get; }

    public RelayCommand ShowAllLevelsCommand { get; }

    /// <summary>
    /// 只看警告、错误、致命
    /// </summary>
    public RelayCommand ShowProblemsOnlyCommand { get; }

    /// <summary>
    /// 显示的日志有变化（新增一批或重新筛选）后触发，视图据此滚动到底部
    /// </summary>
    public event EventHandler? EntriesChanged;

    private LogBuffer Current => _history ?? _live;

    private bool IsFiltered => _shown.Contains(false) || !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>
    /// 一次设置要显示的级别，只重新筛选一次
    /// </summary>
    public void SetShownCategories(IEnumerable<LogCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        Array.Clear(_shown);
        foreach (var category in categories)
            _shown[(int)category] = true;

        foreach (var option in LevelFilters)
            option.SetSelectedSilently(_shown[(int)option.Category]);

        OnLevelFilterChanged();
    }

    /// <summary>
    /// 接入日志源：先显示订阅时刻缓存的最近日志，之后的日志在后台读取并按批追加。只能接入一次。
    /// </summary>
    /// <param name="archive">历史日志；为 null 时只看实时（主控台的最近日志）</param>
    public void Attach(ILogFeed feed, string logDirectory, ILogArchive? archive = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        if (_subscription is not null)
            throw new InvalidOperationException("运行日志页已接入日志源");

        var subscription = feed.Subscribe(out var recent);
        _subscription = subscription;
        LogDirectory = logDirectory;
        Append(recent);

        _ = Task.Run(() => PumpAsync(subscription.Reader));

        if (archive is not null)
        {
            _archive = archive;
            OnPropertyChanged(nameof(CanBrowseHistory));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(ExportFileName));
            _ = RefreshSourcesAsync();
        }
    }

    /// <summary>
    /// 日志目录在设置页切换后调用：更新「打开日志目录」的目标；接入了历史日志时，按日期查看随之改读新目录。须在 UI 线程上调用。
    /// </summary>
    /// <remarks>
    /// 正在查看的是旧目录里的某一天：切回「实时」，不能把旧目录的内容留在界面上当成新目录的。
    /// 旧目录中的日志原地保留，仍可用「打开日志目录」之外的方式查看。
    /// </remarks>
    public void UpdateLogDirectory(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        if (string.Equals(_logDirectory, logDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        LogDirectory = logDirectory;
        if (_archive is null)
            return;

        if (!IsLive)
            _ = SelectSourceAsync(LogSourceOption.Live);

        StatusText = "日志目录已切换，按日期查看改为读取新目录";
        _ = RefreshSourcesAsync();
    }

    /// <summary>
    /// 追加一批实时日志，超出 <see cref="Capacity"/> 时丢弃最旧的。
    /// </summary>
    public void Append(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;

        // 正在查看历史：实时日志只在后台累积，切回「实时」时一并显示
        var visible = _history is null;

        // 一批就超过容量时，前面那些加进来也会马上被挤掉，直接跳过
        var shown = 0;
        for (var i = Math.Max(0, entries.Count - _live.Capacity); i < entries.Count; i++)
        {
            var item = new LogEntryItem(entries[i]);
            _live.Add(item);
            if (visible && Shows(item))
            {
                Entries.Add(item);
                shown++;
            }
        }

        TrimLive(visible);
        if (!visible)
            return;

        // 未读的一定是显示集合末尾那几条：被挤掉的只会是最旧的，所以上限就是当前显示条数
        if (!_autoScroll)
            _unseenCount = Math.Min(_unseenCount + shown, Entries.Count);

        NotifyChanged();
    }

    /// <summary>
    /// 切换来源。选历史日期时在后台读取该天的日志；读取期间再次切换会取消上一次读取，只采用最后一次的结果。
    /// 不抛出：读取失败、超时都以 <see cref="StatusText"/> 提示。
    /// </summary>
    public async Task SelectSourceAsync(LogSourceOption source)
    {
        ArgumentNullException.ThrowIfNull(source);

        CancelLoad();
        SetSelectedSource(source);

        if (source.Date is not { } date || _archive is null)
        {
            _history = null;
            IsLoading = false;
            StatusText = null;
            Refilter();
            return;
        }

        var cts = new CancellationTokenSource(ArchiveTimeout);
        _loadCts = cts;
        _history = new LogBuffer(HistoryCapacity);
        IsLoading = true;
        StatusText = $"正在读取 {date:yyyy-MM-dd} 的日志…";

        // 先清空显示：读取期间不能让人把实时日志误当成那一天的历史
        Refilter();

        Result<LogDay> result;
        try
        {
            result = await _archive.ReadDayAsync(date, HistoryCapacity, cts.Token);
        }
        catch (OperationCanceledException) when (!ReferenceEquals(_loadCts, cts))
        {
            // 已切换到别的来源，这次结果作废
            return;
        }
        catch (OperationCanceledException)
        {
            result = Result<LogDay>.Failure(new Error("Log.ReadTimeout", $"超过 {ArchiveTimeout.TotalSeconds:0} 秒仍未读完"));
        }
        catch (Exception ex)
        {
            // 在 UI 线程上继续执行：异常不能逃逸，否则会触发全站安全跳闸并退出程序
            result = Result<LogDay>.Failure(new Error("Log.ReadFailed", ex.Message));
        }

        if (!ReferenceEquals(_loadCts, cts))
            return;

        _loadCts = null;
        cts.Dispose();
        IsLoading = false;

        if (result.IsFailure)
        {
            StatusText = $"读取失败：{result.Error.Message}";
            Refilter();
            return;
        }

        var day = result.Value;
        foreach (var entry in day.Entries)
            _history.Add(new LogEntryItem(entry));

        StatusText = day.IsTruncated
            ? $"该日共 {day.TotalCount} 条，仅显示最后 {day.Entries.Count} 条"
            : $"该日共 {day.TotalCount} 条";
        Refilter();
    }

    /// <summary>
    /// 重新列出有日志的日期（日志目录可能已新增当天文件或清理了过期文件）。不抛出，失败以 <see cref="StatusText"/> 提示。
    /// </summary>
    public async Task RefreshSourcesAsync()
    {
        if (_archive is null)
            return;

        Result<IReadOnlyList<DateOnly>> dates;
        try
        {
            using var cts = new CancellationTokenSource(ArchiveTimeout);
            dates = await _archive.GetAvailableDatesAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "读取日志目录超时";
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"读取日志目录失败：{ex.Message}";
            return;
        }

        if (dates.IsFailure)
        {
            StatusText = dates.Error.Message;
            return;
        }

        // 正在查看的日期即使刚被清理也保留在列表里，否则下拉框会突然变成空选
        var available = dates.Value.ToList();
        if (_selectedSource.Date is { } selected && !available.Contains(selected))
            available.Add(selected);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var desired = new List<LogSourceOption> { LogSourceOption.Live };
        desired.AddRange(available.OrderByDescending(d => d).Select(d => LogSourceOption.ForDate(d, today)));
        SyncSources(desired);

        // 跨午夜后「（今天）」的标注会移走：换成新的选项实例，不重新读取
        if (_selectedSource.Date is { } current
            && desired.FirstOrDefault(o => o.Date == current) is { } renamed
            && !Equals(renamed, _selectedSource))
        {
            SetSelectedSource(renamed);
        }
    }

    /// <summary>
    /// 把 <see cref="ExportDate"/> 的日志文件写入 <paramref name="destination"/>。不抛出，结果以 <see cref="StatusText"/> 提示。
    /// </summary>
    /// <param name="destinationName">目标文件名，仅用于提示</param>
    public async Task ExportAsync(Stream destination, string destinationName)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_archive is null || _isExporting)
            return;

        var date = ExportDate;
        SetExporting(true);
        StatusText = $"正在导出 {date:yyyy-MM-dd} 的日志…";

        Result result;
        try
        {
            using var cts = new CancellationTokenSource(ArchiveTimeout);
            result = await _archive.ExportDayAsync(date, destination, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = Result.Failure(new Error("Log.ExportTimeout", $"超过 {ArchiveTimeout.TotalSeconds:0} 秒仍未完成"));
        }
        catch (Exception ex)
        {
            result = Result.Failure(new Error("Log.ExportFailed", ex.Message));
        }
        finally
        {
            SetExporting(false);
        }

        StatusText = result.IsSuccess
            ? $"已导出 {date:yyyy-MM-dd} 的日志：{destinationName}"
            : $"导出失败：{result.Error.Message}";
    }

    /// <summary>
    /// 视图在打开保存对话框、创建目标文件时出错（无写权限、U 盘被拔出等），由视图转交提示
    /// </summary>
    public void ReportExportFailure(string reason) => StatusText = $"导出失败：{reason}";

    private bool Shows(LogEntryItem item) => _shown[(int)item.Category] && item.MatchesSearch(_searchText);

    private IReadOnlyList<LogCategory> SelectedCategories()
        => LogCategories.All.Where(c => _shown[(int)c]).ToList();

    private void SetSelectedSource(LogSourceOption source)
    {
        if (!SetProperty(ref _selectedSource, source, nameof(SelectedSource)))
            return;

        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(ExportDate));
        OnPropertyChanged(nameof(ExportFileName));
        OnPropertyChanged(nameof(ExportTip));
        ClearCommand.RaiseCanExecuteChanged();
    }

    private void SetExporting(bool value)
    {
        _isExporting = value;
        OnPropertyChanged(nameof(CanExport));
    }

    private void SyncSources(IReadOnlyList<LogSourceOption> desired)
    {
        // 就地增删而不是整体替换：下拉框保持当前选中项，不闪烁
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Sources[i]))
                Sources.RemoveAt(i);
        }

        // 剩下的与 desired 同序（都是「实时」在前、日期从新到旧），按位置补齐缺的
        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= Sources.Count || !Equals(Sources[i], desired[i]))
                Sources.Insert(i, desired[i]);
        }
    }

    private void CancelLoad()
    {
        // 只取消不释放：读取任务可能仍在使用令牌；超时定时器随取消一起失效
        _loadCts?.Cancel();
        _loadCts = null;
    }

    private void OnLevelFilterChangedByUser()
    {
        foreach (var option in LevelFilters)
            _shown[(int)option.Category] = option.IsSelected;

        OnLevelFilterChanged();
    }

    private void OnLevelFilterChanged()
    {
        OnPropertyChanged(nameof(LevelFilterText));
        OnPropertyChanged(nameof(LevelFilterDetail));
        Refilter();
    }

    private void TrimLive(bool visible)
    {
        var overflow = _live.Items.Count - _live.Capacity;
        if (overflow <= 0)
            return;

        for (var i = 0; i < overflow; i++)
        {
            var removed = _live.Items[i];
            _live.Counts[(int)removed.Category]--;

            // 显示集合是实时缓冲的有序子序列：被挤掉的条目如果在显示，一定排在显示集合最前面
            if (visible && Entries.Count > 0 && ReferenceEquals(Entries[0], removed))
                Entries.RemoveAt(0);
        }

        _live.Items.RemoveRange(0, overflow);
    }

    private void Refilter()
    {
        // 整体替换而不是逐条增删：筛选变化时只触发一次列表重建。列表重建后原来的阅读位置已不存在，未读数随之归零
        Entries = new ObservableCollection<LogEntryItem>(Current.Items.Where(Shows));
        _unseenCount = 0;
        NotifyChanged();
    }

    /// <summary>
    /// 只清空界面上的实时日志，日志文件不受影响；查看历史时不可用
    /// </summary>
    private void Clear()
    {
        if (!IsLive)
            return;

        _live.Clear();
        _unseenCount = 0;
        Entries = [];
        NotifyChanged();
    }

    private void ResetUnseen()
    {
        if (_unseenCount == 0)
            return;

        _unseenCount = 0;
        NotifyUnseen();
    }

    private void NotifyUnseen()
    {
        OnPropertyChanged(nameof(UnseenCount));
        OnPropertyChanged(nameof(HasUnseen));
        OnPropertyChanged(nameof(UnseenText));
    }

    private void NotifyChanged()
    {
        foreach (var option in LevelFilters)
            option.Count = Current.Counts[(int)option.Category];

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        NotifyUnseen();
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    // 退订（Dispose）或日志管线关闭后队列完成，循环自然退出
    private async Task PumpAsync(ChannelReader<LogEntry> reader)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new List<LogEntry>();
            while (reader.TryRead(out var entry))
                batch.Add(entry);

            Dispatcher.UIThread.Post(() => Append(batch));
            await Task.Delay(BatchInterval).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        CancelLoad();
        _subscription?.Dispose();
    }

    /// <summary>
    /// 一组日志及其按级别的计数
    /// </summary>
    private sealed class LogBuffer(int capacity)
    {
        public int Capacity { get; } = capacity;

        public List<LogEntryItem> Items { get; } = new(Math.Min(capacity, 4096));

        public int[] Counts { get; } = new int[LogCategories.All.Count];

        public void Add(LogEntryItem item)
        {
            Items.Add(item);
            Counts[(int)item.Category]++;
        }

        public void Clear()
        {
            Items.Clear();
            Array.Clear(Counts);
        }
    }
}
