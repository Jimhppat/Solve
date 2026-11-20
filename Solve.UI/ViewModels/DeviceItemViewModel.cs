using System;
using System.Collections.Generic;
using System.Globalization;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Domain.Devices;
using Solve.Device.Sdk;

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
        _points = binding?.Driver?.Points ?? [];
    }

    public DeviceDescriptor Device { get; }

    // ==================== 通信状态与读数（由页面定时刷新） ====================

    private readonly IReadOnlyList<PointInfo> _points;
    private ModuleStatus? _status;
    private string _commText = "-";
    private string _commColor = "#94A3B8";
    private string _commProblem = string.Empty;
    private string _readings = string.Empty;

    public string CommText
    {
        get => _commText;
        private set => SetProperty(ref _commText, value);
    }

    public string CommColor
    {
        get => _commColor;
        private set => SetProperty(ref _commColor, value);
    }

    /// <summary>
    /// 非在线时的原因，悬停显示
    /// </summary>
    public string CommProblem
    {
        get => _commProblem;
        private set => SetProperty(ref _commProblem, value);
    }

    /// <summary>
    /// 最近读数，如"输出电压 12.34 V · 输出电流 0.5 A"
    /// </summary>
    public string Readings
    {
        get => _readings;
        private set => SetProperty(ref _readings, value);
    }

    /// <summary>
    /// 用最新的运行状态刷新显示；快照没变时不触发任何属性通知。须在 UI 线程上调用。
    /// </summary>
    public void ApplyStatus(ModuleStatus? status)
    {
        if (ReferenceEquals(status, _status))
            return;

        _status = status;
        (CommText, CommColor) = status?.State switch
        {
            ModuleCommState.Online => ("在线", "#10B981"),
            ModuleCommState.Waiting => ("等待", "#94A3B8"),
            ModuleCommState.NotPolled => ("不轮询", "#94A3B8"),
            ModuleCommState.CommFault => ("通信异常", "#EF4444"),
            ModuleCommState.ConfigError => ("配置错误", "#EF4444"),
            ModuleCommState.DeviceFault => ("设备故障", "#F59E0B"),
            _ => ("-", "#94A3B8"),
        };
        CommProblem = status?.Problem ?? string.Empty;
        Readings = status is null ? string.Empty : ReadingFormat.Module(_points, status.Points);
    }

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
