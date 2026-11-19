using System;
using Avalonia.Controls;
using Solve.UI.ViewModels;

namespace Solve.UI.Views;

public partial class BusModulesWindow : Window
{
    private BusDevicesViewModel? _page;

    public BusModulesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_page is not null)
            _page.CloseRequested -= OnCloseRequested;

        _page = DataContext as BusDevicesViewModel;

        if (_page is not null)
            _page.CloseRequested += OnCloseRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_page is not null)
        {
            _page.CloseRequested -= OnCloseRequested;
            _page = null;
        }

        base.OnClosed(e);
    }

    // 所属总线已被删除：其模块已随之删除，窗口不再有可编辑的对象
    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
