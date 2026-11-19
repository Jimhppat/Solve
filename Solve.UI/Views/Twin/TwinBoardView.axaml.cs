using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Solve.UI.ViewModels.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 主控台显示区。代码后置只做纯视图的两件事：刷新节拍，以及产品信息卡片的打开与收起。
/// </summary>
/// <remarks>
/// <para>刷新节拍：显示区在界面上时按固定周期刷新，离开界面（切到其他页面）即停止，不占 UI 线程。</para>
/// <para>产品信息卡片：点选产品格 → 卡片挂在那一格旁边打开；再点同一格 → 收起；点另一格 → 卡片移到那一格。
/// 在窗口里按下的位置既不是卡片也不是产品格（空白处、容器、切换胶囊、页面其他元素）→ 收起并取消选中。
/// Esc、滚动显示区（卡片会与格子错位）、离开主控台也会收起；视图模型取消选中（切换柜体、拓扑重建）时卡片跟着收起。</para>
/// <para>判断"按在哪里"用窗口级隧道事件，并且包括已处理的事件：按钮、切换胶囊会把按下事件标记为已处理，冒泡收不到。
/// 窗口级处理器只在显示区挂在界面上时登记，离开即移除，窗口不会因此一直引用显示区。</para>
/// </remarks>
public partial class TwinBoardView : UserControl
{
    // 小于半个像素的滚动（布局取整的抖动）不算滚动
    private const double ScrollTolerance = 0.5;

    private readonly DispatcherTimer _timer;
    private TopLevel? _topLevel;
    private TwinBoardViewModel? _board;

    public TwinBoardView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TwinBoardViewModel.RefreshInterval };
        _timer.Tick += OnTick;

        // 自身与子元素的事件，随控件一起回收
        AddHandler(FixtureNodeView.ProductTappedEvent, OnProductTapped);
        Scroller.ScrollChanged += OnScrollChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        WatchBoard(DataContext as TwinBoardViewModel);
        _board?.Refresh();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        CloseCard(); // 离开主控台：卡片不能留在别的页面上
        _topLevel?.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;
        WatchBoard(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        CloseCard();
        if (_topLevel is not null)
            WatchBoard(DataContext as TwinBoardViewModel);
    }

    private void OnTick(object? sender, EventArgs e) => _board?.Refresh();

    // ==================== 产品信息卡片 ====================

    private void OnProductTapped(object? sender, ProductTappedEventArgs e)
    {
        e.Handled = true;
        if (_board is not { } board || e.Source is not Control fixture)
            return;

        // 点未选中的格子：卡片挂到这一格打开（换格时先关再开，弹出层只在打开时按挂靠位置计算自己的位置）；
        // 再点已打开的那一格：收起
        ProductCard.IsOpen = false;
        ProductCard.PlacementTarget = fixture;
        ProductCard.PlacementRect = e.Cell;
        board.ToggleSelection(e.Product.Id);
        ProductCard.IsOpen = board.HasSelection;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ProductCard.IsOpen || e.Source is not Visual source)
            return;

        // 按在卡片上（弹出层画在窗口的覆盖层里时，卡片上的按下也会经过窗口）：不收起
        if (ProductCard.Child is Visual card && (ReferenceEquals(source, card) || card.IsVisualAncestorOf(source)))
            return;

        // 按在产品格上：交给点选处理（同一格保持，另一格换卡片）
        if (source.FindAncestorOfType<FixtureNodeView>(includeSelf: true) is { } fixture && fixture.ProductAt(e.GetPosition(fixture)) is not null)
            return;

        CloseCard();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // 不标记为已处理：Esc 仍会继续传给其他控件（例如以后加的对话框）
        if (e.Key == Key.Escape && ProductCard.IsOpen)
            CloseCard();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 卡片按打开时的位置画在格子旁，显示区滚动后会与格子错位
        if (ProductCard.IsOpen && (Math.Abs(e.OffsetDelta.X) > ScrollTolerance || Math.Abs(e.OffsetDelta.Y) > ScrollTolerance))
            CloseCard();
    }

    private void OnBoardChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 视图模型取消了选中（切换柜体、拓扑重建）：卡片所指的格子已不在画面上
        if (e.PropertyName == nameof(TwinBoardViewModel.HasSelection) && _board is { HasSelection: false })
            ProductCard.IsOpen = false;
    }

    private void CloseCard()
    {
        ProductCard.IsOpen = false;
        _board?.ClearSelection();
    }

    private void WatchBoard(TwinBoardViewModel? board)
    {
        if (ReferenceEquals(board, _board))
            return;

        if (_board is not null)
            _board.PropertyChanged -= OnBoardChanged;

        _board = board;
        if (_board is not null)
            _board.PropertyChanged += OnBoardChanged;
    }
}
