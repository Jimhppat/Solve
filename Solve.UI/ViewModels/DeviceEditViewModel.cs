using System;
using System.Globalization;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.UI.ViewModels;

/// <summary>
/// 新增 / 编辑设备弹窗的表单。地址以文本录入，保存时解析，非整数直接在表单上报错，不触达存储。
/// </summary>
public sealed class DeviceEditViewModel : ViewModelBase
{
    private bool _isCreateMode = true;
    private DeviceId _id;
    private string _name = string.Empty;
    private string _addressText = string.Empty;
    private string _remark = string.Empty;
    private string? _errorMessage;

    public bool IsCreateMode
    {
        get => _isCreateMode;
        private set
        {
            if (SetProperty(ref _isCreateMode, value))
                OnPropertyChanged(nameof(Title));
        }
    }

    public string Title => IsCreateMode ? "新增模块" : "编辑模块";

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

    public void ResetForCreate(int suggestedAddress)
    {
        IsCreateMode = true;
        _id = DeviceId.New();
        Name = "新模块";
        AddressText = suggestedAddress.ToString(CultureInfo.InvariantCulture);
        Remark = string.Empty;
        ErrorMessage = null;
    }

    public void LoadFrom(DeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);

        IsCreateMode = false;
        _id = device.Id;
        Name = device.Name;
        AddressText = device.Address.ToString(CultureInfo.InvariantCulture);
        Remark = device.Remark ?? string.Empty;
        ErrorMessage = null;
    }

    /// <summary>
    /// 由表单构造设备描述并做领域校验；失败时把全部错误显示在表单上
    /// </summary>
    public Result<DeviceDescriptor> BuildDescriptor(BusId busId)
    {
        if (!int.TryParse(AddressText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var address))
            return Fail(DeviceErrors.InvalidField("Address", "地址必须是整数。"));

        var remark = Remark?.Trim();
        var device = new DeviceDescriptor(_id, busId, Name?.Trim() ?? string.Empty, address)
        {
            Remark = string.IsNullOrEmpty(remark) ? null : remark,
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
