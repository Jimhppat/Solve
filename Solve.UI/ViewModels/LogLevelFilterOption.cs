namespace Solve.UI.ViewModels;

/// <summary>
/// 级别筛选面板中的一项：一个级别、是否显示、当前保留的条数。
/// </summary>
public sealed class LogLevelFilterOption : ViewModelBase
{
    private readonly Action _changedByUser;
    private bool _isSelected;
    private int _count;

    internal LogLevelFilterOption(LogCategory category, bool isSelected, Action changedByUser)
    {
        Category = category;
        _isSelected = isSelected;
        _changedByUser = changedByUser;
    }

    public LogCategory Category { get; }

    public string Label => Category.DisplayName();

    /// <summary>
    /// 勾选框绑定。用户勾选或取消时通知所属列表重新筛选
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _changedByUser();
        }
    }

    /// <summary>
    /// 该级别在界面中保留的条数（不受筛选影响）
    /// </summary>
    public int Count
    {
        get => _count;
        internal set => SetProperty(ref _count, value);
    }

    // 级别标签配色，与日志行一致
    public bool IsDebug => Category == LogCategory.Debug;

    public bool IsWarning => Category == LogCategory.Warning;

    public bool IsError => Category >= LogCategory.Error;

    public bool IsCritical => Category == LogCategory.Critical;

    /// <summary>
    /// 批量设置（「全部」「仅异常」）时使用：只通知界面，不逐项触发重新筛选，由调用方统一筛选一次
    /// </summary>
    internal void SetSelectedSilently(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}
