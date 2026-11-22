using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

/// <summary>
/// 硬件映射页，数据上下文为 <see cref="HardwareMappingViewModel"/>
/// </summary>
/// <remarks>
/// 两侧栏宽由视图模型保存（拖动结束时写入设置）：ColumnDefinition 不在逻辑树里、不能可靠绑定，
/// 所以在这里把视图模型的栏宽套到列上，拖完再把实际宽度交回视图模型。
/// </remarks>
public partial class HardwareMappingView : UserControl
{
    private HardwareMappingViewModel? _viewModel;

    public HardwareMappingView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as HardwareMappingViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyColumnWidths();
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // 页面切走时解除订阅：视图模型终身存在，视图会被重新创建
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
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
}
