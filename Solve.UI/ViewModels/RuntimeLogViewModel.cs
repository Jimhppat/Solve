using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia.Threading;
using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;

namespace Solve.UI.ViewModels;

/// <summary>
/// 运行日志列表：订阅实时日志，按级别（五类，可多选）与关键字筛选。
/// 运行日志页与主控台的最近日志控件各用一个实例，各自订阅、互不影响。
/// </summary>
/// <remarks>
/// <para>只是一个窗口，完整记录以日志文件为准：界面最多保留最近 <see cref="Capacity"/> 条，清空也只清界面。</para>
/// <para>新日志在后台按批取出、每 <see cref="BatchInterval"/> 最多向 UI 线程投递一次，日志密集时界面刷新频率不随之上升。</para>
/// <para>除 <see cref="Attach"/> 中启动的后台读取循环外，所有成员都只能在 UI 线程上使用。</para>
/// </remarks>
public sealed class RuntimeLogViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 运行日志页默认保留的条数
    /// </summary>
    public const int DefaultCapacity = 2_000;

    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<LogEntryItem> _all;

    // 按 LogCategory 下标：是否显示、保留条数。筛选在热路径上，用数组而不是逐项查找
    private readonly bool[] _shown = new bool[LogCategories.All.Count];
    private readonly int[] _counts = new int[LogCategories.All.Count];

    private ObservableCollection<LogEntryItem> _entries = [];
    private EventSubscription<LogEntry>? _subscription;
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

    /// <param name="capacity">界面保留的最多条数，超出时丢弃最旧的</param>
    /// <param name="shownCategories">初始显示的级别</param>
    public RuntimeLogViewModel(int capacity, IEnumerable<LogCategory> shownCategories)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(shownCategories);
        Capacity = capacity;
        _all = new List<LogEntryItem>(capacity);

        foreach (var category in shownCategories)
            _shown[(int)category] = true;

        LevelFilters = LogCategories.All
            .Select(c => new LogLevelFilterOption(c, _shown[(int)c], OnLevelFilterChangedByUser))
            .ToArray();

        ClearCommand = new RelayCommand(Clear);
        JumpToLatestCommand = new RelayCommand(() => AutoScroll = true);
        ShowAllLevelsCommand = new RelayCommand(() => SetShownCategories(LogCategories.All));
        ShowProblemsOnlyCommand = new RelayCommand(() => SetShownCategories(LogCategories.Problems));
    }

    /// <summary>
    /// 界面保留的最多条数，超出时丢弃最旧的
    /// </summary>
    public int Capacity { get; }

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

    public int TotalCount => _all.Count;

    public int WarningCount => _counts[(int)LogCategory.Warning];

    /// <summary>
    /// 错误与致命合计
    /// </summary>
    public int ErrorCount => _counts[(int)LogCategory.Error] + _counts[(int)LogCategory.Critical];

    public string SummaryText
    {
        get
        {
            var summary = $"共 {_all.Count} 条 · 警告 {WarningCount} · 错误 {ErrorCount}";
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
            return _all.Count == 0 ? "暂无日志" : "没有符合筛选条件的日志";
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
    public void Attach(ILogFeed feed, string logDirectory)
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
    }

    /// <summary>
    /// 追加一批日志，超出 <see cref="Capacity"/> 时丢弃最旧的。
    /// </summary>
    public void Append(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;

        // 一批就超过容量时，前面那些加进来也会马上被挤掉，直接跳过
        var shown = 0;
        for (var i = Math.Max(0, entries.Count - Capacity); i < entries.Count; i++)
        {
            var item = new LogEntryItem(entries[i]);
            _all.Add(item);
            _counts[(int)item.Category]++;
            if (Shows(item))
            {
                Entries.Add(item);
                shown++;
            }
        }

        TrimToCapacity();

        // 未读的一定是显示集合末尾那几条：被挤掉的只会是最旧的，所以上限就是当前显示条数
        if (!_autoScroll)
            _unseenCount = Math.Min(_unseenCount + shown, Entries.Count);

        NotifyChanged();
    }

    private bool Shows(LogEntryItem item) => _shown[(int)item.Category] && item.MatchesSearch(_searchText);

    private IReadOnlyList<LogCategory> SelectedCategories()
        => LogCategories.All.Where(c => _shown[(int)c]).ToList();

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

    private void TrimToCapacity()
    {
        var overflow = _all.Count - Capacity;
        if (overflow <= 0)
            return;

        for (var i = 0; i < overflow; i++)
        {
            var removed = _all[i];
            _counts[(int)removed.Category]--;

            // 显示集合是 _all 的有序子序列：被挤掉的条目如果在显示，一定排在显示集合最前面
            if (Entries.Count > 0 && ReferenceEquals(Entries[0], removed))
                Entries.RemoveAt(0);
        }

        _all.RemoveRange(0, overflow);
    }

    private void Refilter()
    {
        // 整体替换而不是逐条增删：筛选变化时只触发一次列表重建。列表重建后原来的阅读位置已不存在，未读数随之归零
        Entries = new ObservableCollection<LogEntryItem>(_all.Where(Shows));
        _unseenCount = 0;
        NotifyChanged();
    }

    /// <summary>
    /// 只清空界面上的日志，日志文件不受影响
    /// </summary>
    private void Clear()
    {
        _all.Clear();
        Array.Clear(_counts);
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
            option.Count = _counts[(int)option.Category];

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
        _subscription?.Dispose();
    }
}
