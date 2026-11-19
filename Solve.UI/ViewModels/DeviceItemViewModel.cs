using System;
using Solve.Core.Application.Devices;
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
        DriverBinding? binding,
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
        BindingProblem = binding?.Problem ?? string.Empty;
        (BindingText, BindingColor) = binding?.Status switch
        {
            DriverBindingStatus.Ready => ("就绪", "#10B981"),
            DriverBindingStatus.NoProtocol => ("无协议", "#94A3B8"),
            DriverBindingStatus.DriverNotLoaded => ("驱动未加载", "#EF4444"),
            DriverBindingStatus.AddressOutOfRange => ("地址越界", "#EF4444"),
            DriverBindingStatus.InvalidParameters => ("参数无效", "#EF4444"),
            DriverBindingStatus.SessionFailed => ("会话创建失败", "#EF4444"),

            // 刚保存、尚未重新绑定（极短的窗口），或总线已不在注册表中
            _ => ("未绑定", "#F59E0B"),
        };
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

    /// <summary>
    /// 驱动状态：就绪 / 无协议 / 驱动未加载 / 地址越界 / 参数无效 / 会话创建失败
    /// </summary>
    public string BindingText { get; }

    public string BindingColor { get; }

    /// <summary>
    /// 未就绪的原因，悬停显示；就绪时为空串
    /// </summary>
    public string BindingProblem { get; }

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
