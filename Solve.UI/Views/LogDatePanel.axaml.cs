using Avalonia.Controls;
using Avalonia.Interactivity;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 日期面板，数据上下文为 <see cref="RuntimeLogViewModel"/>。选定日期或「实时」后触发 <see cref="Picked"/>，由所在页面关闭浮层。
/// </summary>
/// <remarks>
/// 选择交给视图模型：读取在后台进行、不抛出（失败以状态提示显示），连续点选只采用最后一次。
/// </remarks>
public partial class LogDatePanel : UserControl
{
    public LogDatePanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 已选定要查看的来源
    /// </summary>
    public event EventHandler? Picked;

    private void OnDayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RuntimeLogViewModel vm || (sender as Control)?.DataContext is not LogCalendarDay day)
            return;

        // 按钮已按 CanSelect 置灰；这里再拦一次，防止格子刷新与点击交错时点到刚失效的日期
        if (!vm.CanSelectDate(day.Date))
            return;

        _ = vm.SelectDateAsync(day.Date);
        Picked?.Invoke(this, EventArgs.Empty);
    }

    private void OnShowLive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RuntimeLogViewModel vm)
            return;

        _ = vm.ShowLiveAsync();
        Picked?.Invoke(this, EventArgs.Empty);
    }

    private void OnShowToday(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RuntimeLogViewModel vm || !vm.Calendar.TodayHasLog)
            return;

        _ = vm.ShowTodayAsync();
        Picked?.Invoke(this, EventArgs.Empty);
    }
}
