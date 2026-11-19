using System;
using Solve.Core.Domain.Devices;

namespace Solve.UI.ViewModels;

/// <summary>
/// 设备表中的一行。配置不可变：修改保存后整表重新读取，不在行上就地改值。
/// </summary>
public sealed class DeviceItemViewModel : ViewModelBase
{
    public DeviceItemViewModel(
        DeviceDescriptor device,
        Action<DeviceItemViewModel> onEdit,
        Action<DeviceItemViewModel> onDelete)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(onEdit);
        ArgumentNullException.ThrowIfNull(onDelete);

        Device = device;
        EditCommand = new RelayCommand(() => onEdit(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    public DeviceDescriptor Device { get; }

    public int Address => Device.Address;

    public string Name => Device.Name;

    public string Remark => Device.Remark ?? string.Empty;

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }
}
