using System.Globalization;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;

namespace Solve.UI.ViewModels;

/// <summary>
/// 模块配置窗口右上的"添加模块"表单：选中的驱动 + 名称、起始地址、数量、备注。
/// 起始地址与数量以文本录入，保存时解析；地址预览随输入即时更新，便于确认将占用哪些地址。
/// </summary>
public sealed class ModuleBatchFormViewModel : ViewModelBase
{
    private DriverItemViewModel? _driver;
    private string _name = string.Empty;
    private string _startAddressText = string.Empty;
    private string _countText = "1";
    private string _remark = string.Empty;
    private string? _errorMessage;

    public DriverItemViewModel? Driver
    {
        get => _driver;
        private set
        {
            if (SetProperty(ref _driver, value))
            {
                OnPropertyChanged(nameof(HasDriver));
                OnPropertyChanged(nameof(DriverText));
                OnPropertyChanged(nameof(AddressPreview));
            }
        }
    }

    public bool HasDriver => _driver is not null;

    public string DriverText => _driver is null
        ? "未选择驱动"
        : $"{_driver.Name} · {_driver.CategoryName} · 地址 {_driver.AddressRange}";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string StartAddressText
    {
        get => _startAddressText;
        set
        {
            if (SetProperty(ref _startAddressText, value))
                OnPropertyChanged(nameof(AddressPreview));
        }
    }

    public string CountText
    {
        get => _countText;
        set
        {
            if (SetProperty(ref _countText, value))
                OnPropertyChanged(nameof(AddressPreview));
        }
    }

    public string Remark
    {
        get => _remark;
        set => SetProperty(ref _remark, value);
    }

    /// <summary>
    /// 将占用的地址，如"地址 3 – 6，共 4 个"；输入不完整时为 "-"
    /// </summary>
    public string AddressPreview
    {
        get
        {
            if (_driver is null || !TryParse(_startAddressText, out var start) || !TryParse(_countText, out var count) || count < 1)
                return "-";

            var last = (long)start + count - 1;
            return count == 1
                ? $"地址 {start}，共 1 个"
                : $"地址 {start} – {last}，共 {count} 个";
        }
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

    /// <summary>
    /// 选中驱动：名称预填为驱动名，起始地址预填为该驱动范围内最小的空闲地址，数量回到 1
    /// </summary>
    public void SelectDriver(DriverItemViewModel? driver, int suggestedStartAddress)
    {
        var changed = !ReferenceEquals(_driver, driver);
        Driver = driver;
        ErrorMessage = null;
        if (driver is null)
            return;

        if (changed)
        {
            Name = driver.Name;
            CountText = "1";
            Remark = string.Empty;
        }

        StartAddressText = suggestedStartAddress.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 添加成功后保留驱动、名称与数量，起始地址换成下一个空闲地址，便于连续添加
    /// </summary>
    public void AfterAdded(int suggestedStartAddress)
    {
        StartAddressText = suggestedStartAddress.ToString(CultureInfo.InvariantCulture);
        ErrorMessage = null;
    }

    public Result<DeviceBatchRequest> BuildRequest(BusId busId)
    {
        if (_driver is null)
            return Fail(DeviceErrors.InvalidField("Driver", "请先在左侧驱动表中选择驱动。"));

        if (!TryParse(_startAddressText, out var start))
            return Fail(DeviceErrors.InvalidField("StartAddress", "起始地址必须是整数。"));

        if (!TryParse(_countText, out var count))
            return Fail(DeviceErrors.InvalidField("Count", "数量必须是整数。"));

        ErrorMessage = null;
        return Result<DeviceBatchRequest>.Success(
            new DeviceBatchRequest(busId, _driver.Driver.Id, _name?.Trim() ?? string.Empty, start, count) { Remark = _remark });
    }

    private Result<DeviceBatchRequest> Fail(Error error)
    {
        ErrorMessage = error.Message;
        return Result<DeviceBatchRequest>.Failure(error);
    }

    private static bool TryParse(string? text, out int value)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
