using System;
using Solve.Core.Domain.Devices;

namespace Solve.UI.ViewModels;

/// <summary>
/// 已挂载模块表中的一行。配置不可变：修改保存后整表重新读取，不在行上就地改值；勾选状态只用于批量删除。
/// </summary>
public sealed class DeviceItemViewModel : ViewModelBase
{
    private readonly Action _onCheckedChanged;
    private bool _isChecked;

    public DeviceItemViewModel(
        DeviceDescriptor device,
        DriverItemViewModel driver,
        Action<DeviceItemViewModel> onEdit,
        Action<DeviceItemViewModel> onDelete,
        Action onCheckedChanged)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(onEdit);
        ArgumentNullException.ThrowIfNull(onDelete);
        ArgumentNullException.ThrowIfNull(onCheckedChanged);

        Device = device;
        Driver = driver;
        _onCheckedChanged = onCheckedChanged;
        EditCommand = new RelayCommand(() => onEdit(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    public DeviceDescriptor Device { get; }

    /// <summary>
    /// 使用的驱动；驱动已从程序中移除时是名为"未知驱动"的占位项
    /// </summary>
    public DriverItemViewModel Driver { get; }

    public int Address => Device.Address;

    public string Name => Device.Name;

    public string DriverName => Driver.Name;

    public string Remark => Device.Remark ?? string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _onCheckedChanged();
        }
    }

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }
}
