using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;

namespace Solve.UI.ViewModels;

/// <summary>
/// 级别筛选项：显示不低于 <paramref name="Minimum"/> 的日志
/// </summary>
public sealed record LogLevelOption(string Label, LogLevel Minimum)
{
    public override string ToString() => Label;
}

/// <summary>
/// 运行日志列表：订阅实时日志，按级别与关键字筛选。运行日志页与主控台的最近日志控件各用一个实例，各自订阅、互不影响。
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
    private ObservableCollection<LogEntryItem> _entries = [];
    private EventSubscription<LogEntry>? _subscription;
    private LogLevelOption _selectedLevel;
    private string _searchText = string.Empty;
    private bool _autoScroll = true;
    private string? _logDirectory;
    private int _warningCount;
    private int _errorCount;

    /// <summary>
    /// 运行日志页（也供 XAML 设计器使用）：保留 <see cref="DefaultCapacity"/> 条，显示全部级别
    /// </summary>
    public RuntimeLogViewModel() : this(DefaultCapacity, LogLevel.Trace)
    {
    }

    /// <param name="capacity">界面保留的最多条数，超出时丢弃最旧的</param>
    /// <param name="minimumLevel">初始级别筛选，必须是 <see cref="LevelOptions"/> 中某一项的下限</param>
    public RuntimeLogViewModel(int capacity, LogLevel minimumLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _all = new List<LogEntryItem>(capacity);

        LevelOptions =
        [
            new LogLevelOption("全部级别", LogLevel.Trace),
            new LogLevelOption("信息及以上", LogLevel.Information),
            new LogLevelOption("警告及以上", LogLevel.Warning),
            new LogLevelOption("仅错误", LogLevel.Error),
        ];
        _selectedLevel = LevelOptions.FirstOrDefault(o => o.Minimum == minimumLevel)
                         ?? throw new ArgumentOutOfRangeException(nameof(minimumLevel), minimumLevel, "没有对应的级别筛选项");
        ClearCommand = new RelayCommand(Clear);
    }

    /// <summary>
    /// 界面保留的最多条数，超出时丢弃最旧的
    /// </summary>
    public int Capacity { get; }

    public string PageTitle => "运行日志";

    public IReadOnlyList<LogLevelOption> LevelOptions { get; }

    /// <summary>
    /// 筛选后显示的日志，按时间顺序
    /// </summary>
    public ObservableCollection<LogEntryItem> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    public LogLevelOption SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            // 下拉框切换数据源时可能短暂写入 null
            if (SetProperty(ref _selectedLevel, value ?? LevelOptions[0]))
                Refilter();
        }
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
    /// 有新日志时是否自动滚动到最后一条
    /// </summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

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

    public int WarningCount => _warningCount;

    public int ErrorCount => _errorCount;

    public string SummaryText
    {
        get
        {
            var summary = $"共 {_all.Count} 条 · 警告 {_warningCount} · 错误 {_errorCount}";
            return IsFiltered ? $"{summary} · 筛选出 {Entries.Count} 条" : summary;
        }
    }

    public bool IsEmpty => Entries.Count == 0;

    public string EmptyText => _all.Count == 0 ? "暂无日志" : "没有符合筛选条件的日志";

    public RelayCommand ClearCommand { get; }

    /// <summary>
    /// 显示的日志有变化（新增一批或重新筛选）后触发，视图据此滚动到底部
    /// </summary>
    public event EventHandler? EntriesChanged;

    private bool IsFiltered => _selectedLevel.Minimum > LogLevel.Trace || !string.IsNullOrWhiteSpace(_searchText);

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
        for (var i = Math.Max(0, entries.Count - Capacity); i < entries.Count; i++)
        {
            var item = new LogEntryItem(entries[i]);
            _all.Add(item);
            Tally(item, +1);
            if (item.Matches(_selectedLevel.Minimum, _searchText))
                Entries.Add(item);
        }

        TrimToCapacity();
        NotifyChanged();
    }

    private void TrimToCapacity()
    {
        var overflow = _all.Count - Capacity;
        if (overflow <= 0)
            return;

        for (var i = 0; i < overflow; i++)
        {
            var removed = _all[i];
            Tally(removed, -1);

            // 显示集合是 _all 的有序子序列：被挤掉的条目如果在显示，一定排在显示集合最前面
            if (Entries.Count > 0 && ReferenceEquals(Entries[0], removed))
                Entries.RemoveAt(0);
        }

        _all.RemoveRange(0, overflow);
    }

    private void Refilter()
    {
        // 整体替换而不是逐条增删：筛选变化时只触发一次列表重建
        Entries = new ObservableCollection<LogEntryItem>(_all.Where(i => i.Matches(_selectedLevel.Minimum, _searchText)));
        NotifyChanged();
    }

    /// <summary>
    /// 只清空界面上的日志，日志文件不受影响
    /// </summary>
    private void Clear()
    {
        _all.Clear();
        _warningCount = 0;
        _errorCount = 0;
        Entries = [];
        NotifyChanged();
    }

    private void Tally(LogEntryItem item, int delta)
    {
        if (item.IsWarning)
            _warningCount += delta;
        else if (item.IsError)
            _errorCount += delta;
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
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
