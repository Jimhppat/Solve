using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 硬件映射页，数据上下文为 <see cref="HardwareMappingViewModel"/>
/// </summary>
/// <remarks>
/// <para>两侧栏宽由视图模型保存（拖动结束时写入设置）：ColumnDefinition 不在逻辑树里、不能可靠绑定，
/// 所以在这里把视图模型的栏宽套到列上，拖完再把实际宽度交回视图模型。</para>
/// <para>拖拽（右边的模块 / 通道 → 中间接线区）用页面内的指针捕获实现，不走系统拖放：
/// 按下记起点 → 移动超过阈值才开始（单击照常选中模块）→ 捕获指针，按接线区的边界判定是否在放置区内 →
/// 松开时交给视图模型。Esc、失去捕获、离开页面都取消。这里只翻译指针事件，状态与数据变化都在视图模型里。</para>
/// </remarks>
public partial class HardwareMappingView : UserControl
{
    /// <summary>
    /// 按下后移动超过这个距离（设备无关像素）才算拖拽，避免手抖把单击变成拖拽
    /// </summary>
    private const double DragThreshold = 6;

    /// <summary>
    /// 拖拽预览相对指针的偏移：放在指针右下，不遮住指针下方的目标
    /// </summary>
    private static readonly Vector GhostOffset = new(14, 12);

    private HardwareMappingViewModel? _viewModel;

    // 按下了一个可拖的模块 / 通道、还没超过阈值
    private object? _pressedItem;
    private Point _pressPoint;
    private IPointer? _dragPointer;

    // 正在松开（主动释放捕获）：此时的失去捕获不算取消
    private bool _releasing;

    public HardwareMappingView()
    {
        InitializeComponent();

        // 模块行、通道卡在 ListBox 里，ListBoxItem 会把按下事件标为已处理：这里要连已处理的一起收
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        CancelDrag();
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as HardwareMappingViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyColumnWidths();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 页面切走时解除订阅：视图模型终身存在，视图会被重新创建。拖到一半切走也不留预览与高亮
        CancelDrag();
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyColumnWidths();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HardwareMappingViewModel.TreeWidth) or nameof(HardwareMappingViewModel.HardwareWidth))
            ApplyColumnWidths();
    }

    // 列：0 位置树、1 分隔条、2 接线区、3 分隔条、4 硬件通道
    private ColumnDefinition TreeColumn => Columns.ColumnDefinitions[0];

    private ColumnDefinition HardwareColumn => Columns.ColumnDefinitions[4];

    private void ApplyColumnWidths()
    {
        if (_viewModel is null)
            return;

        TreeColumn.Width = new GridLength(_viewModel.TreeWidth);
        HardwareColumn.Width = new GridLength(_viewModel.HardwareWidth);
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
        => _viewModel?.SetColumnWidths(TreeColumn.ActualWidth, HardwareColumn.ActualWidth);

    // ==================== 拖拽 ====================

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedItem = null;
        if (_viewModel is not { IsDragging: false } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.Source is not Visual source || IsInteractive(source))
            return;

        // 只有右边硬件栏里的模块行、通道卡可以拖
        var item = (source as StyledElement)?.DataContext;
        if (item is not (MappingModuleOption or MappingChannelViewModel) || !HardwarePanel.IsVisualAncestorOf(source))
            return;

        _pressedItem = item;
        _pressPoint = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;

        if (!vm.IsDragging)
        {
            if (_pressedItem is null)
                return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pressedItem = null;
                return;
            }

            var moved = e.GetPosition(this) - _pressPoint;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
                return;

            var item = _pressedItem;
            _pressedItem = null;
            if (!vm.BeginDrag(item))
                return;

            // 捕获到页面：指针离开模块列表后移动、松开事件仍然送到这里
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        var position = e.GetPosition(DragLayer) + GhostOffset;
        vm.UpdateDrag(position.X, position.Y, IsOverDropArea(e));
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedItem = null;
        if (_viewModel is not { IsDragging: true } vm)
            return;

        var over = IsOverDropArea(e);
        ReleaseCapture();
        e.Handled = true;

        // 视图模型先清拖拽状态再执行放置，异常在里面接住，不会逃逸到 UI 线程
        _ = vm.CompleteDragAsync(over);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // 被系统或其他控件夺走捕获（如弹出对话框、窗口失去焦点）：取消，不留残影
        if (!_releasing && _viewModel is { IsDragging: true })
            CancelDrag();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _viewModel is not { IsDragging: true })
            return;

        CancelDrag();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        _pressedItem = null;
        ReleaseCapture();
        _viewModel?.CancelDrag();
    }

    private void ReleaseCapture()
    {
        if (_dragPointer is not { } pointer)
            return;

        _dragPointer = null;
        _releasing = true;
        try
        {
            if (ReferenceEquals(pointer.Captured, this))
                pointer.Capture(null);
        }
        finally
        {
            _releasing = false;
        }
    }

    /// <summary>
    /// 指针在中间接线区的边界内（接线区可见时）。按边界判定，不依赖指针下方的命中测试：预览层不接收指针，也不会挡住判定
    /// </summary>
    private bool IsOverDropArea(PointerEventArgs e)
    {
        if (!WiringArea.IsEffectivelyVisible)
            return false;

        return new Rect(WiringArea.Bounds.Size).Contains(e.GetPosition(WiringArea));
    }

    /// <summary>
    /// 按在按钮、勾选框、输入框、滚动条上：交给控件本身（"关联""定位"照常点击），不开始拖拽
    /// </summary>
    private static bool IsInteractive(Visual source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case Button or ToggleButton or TextBox or ScrollBar:
                    return true;
                case ListBoxItem or HardwareMappingView:
                    return false;
            }
        }

        return false;
    }
}
