using System;
using System.Collections.Generic;
using System.Globalization;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.UI.ViewModels;

/// <summary>
/// 编辑单个已挂载模块的表单：驱动、名称、地址、备注、参数、通道都可改，所属总线不可改。
/// 地址以文本录入，保存时解析，非整数直接在表单上报错，不触达存储。
/// </summary>
public sealed class DeviceEditViewModel : ViewModelBase
{
    private DeviceDescriptor? _original;
    private IReadOnlyList<DriverItemViewModel> _drivers = [];
    private DriverItemViewModel? _selectedDriver;
    private string _name = string.Empty;
    private string _addressText = string.Empty;
    private string _remark = string.Empty;
    private string? _errorMessage;

    /// <summary>
    /// 可选的驱动：驱动目录全部驱动；原驱动已从程序中移除时，其占位项排在最前，保证不改驱动也能保存
    /// </summary>
    public IReadOnlyList<DriverItemViewModel> Drivers
    {
        get => _drivers;
        private set => SetProperty(ref _drivers, value);
    }

    public DriverItemViewModel? SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (!SetProperty(ref _selectedDriver, value) || value is null)
                return;

            // 换驱动后参数与通道按新驱动的声明重新生成；换回原驱动时恢复原来的值
            var same = _original is not null && value.Driver.Id == _original.Driver;
            Parameters.Load(value.Parameters, same ? _original!.Parameters : ParameterValues.Empty);
            Channels.Load(value.Driver, same ? _original!.Channels : ModuleChannelConfig.Empty);
        }
    }

    /// <summary>
    /// 所选驱动声明的模块参数
    /// </summary>
    public ParameterFormViewModel Parameters { get; } = new();

    /// <summary>
    /// 这台模块启用哪几路通道、通道别名；通用模块在这里定义通道
    /// </summary>
    public ChannelFormViewModel Channels { get; } = new();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public string Remark
    {
        get => _remark;
        set => SetProperty(ref _remark, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <param name="drivers">可选驱动，须包含 <paramref name="currentDriver"/></param>
    public void LoadFrom(DeviceDescriptor device, IReadOnlyList<DriverItemViewModel> drivers, DriverItemViewModel currentDriver)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(currentDriver);

        _original = device;
        Drivers = drivers;
        SelectedDriver = currentDriver;

        // 再次打开同一模块时 SelectedDriver 未变，不会触发重新生成，这里显式载入本模块的参数
        Parameters.Load(currentDriver.Parameters, device.Parameters);
        Channels.Load(currentDriver.Driver, device.Channels);
        Name = device.Name;
        AddressText = device.Address.ToString(CultureInfo.InvariantCulture);
        Remark = device.Remark ?? string.Empty;
        ErrorMessage = null;
    }

    /// <summary>
    /// 由表单构造修改后的模块描述并做领域校验；失败时把全部错误显示在表单上
    /// </summary>
    public Result<DeviceDescriptor> BuildDescriptor()
    {
        if (_original is null)
            throw new InvalidOperationException("编辑表单尚未载入模块。");

        if (_selectedDriver is null)
            return Fail(DeviceErrors.InvalidField("Driver", "请选择驱动。"));

        if (!int.TryParse(AddressText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var address))
            return Fail(DeviceErrors.InvalidField("Address", "地址必须是整数。"));

        // 驱动未加载（没有参数声明）且没换驱动时，原样保留已保存的参数，不因无法核对而丢失
        var keepOriginal = _selectedDriver.Driver.Id == _original.Driver && _selectedDriver.Parameters.Count == 0;
        var parameters = keepOriginal ? Result<ParameterValues>.Success(_original.Parameters) : Parameters.Collect();
        if (parameters.IsFailure)
            return Fail(parameters.Error);

        // 驱动已不在目录中且没换驱动时，不知道它有哪些通道：原样保留已保存的通道配置
        var channels = _selectedDriver.IsUnknown && _selectedDriver.Driver.Id == _original.Driver
            ? Result<ModuleChannelConfig>.Success(_original.Channels)
            : Channels.Collect();
        if (channels.IsFailure)
            return Fail(channels.Error);

        var remark = Remark?.Trim();
        var device = _original with
        {
            Driver = _selectedDriver.Driver.Id,
            Name = Name?.Trim() ?? string.Empty,
            Address = address,
            Remark = string.IsNullOrEmpty(remark) ? null : remark,
            Parameters = parameters.Value,
            Channels = channels.Value,
        };

        var errors = device.Validate();
        if (errors.Count > 0)
            return Fail(DeviceErrors.InvalidDescriptor(errors));

        ErrorMessage = null;
        return Result<DeviceDescriptor>.Success(device);
    }

    private Result<DeviceDescriptor> Fail(Error error)
    {
        ErrorMessage = error.Message;
        return Result<DeviceDescriptor>.Failure(error);
    }
}
