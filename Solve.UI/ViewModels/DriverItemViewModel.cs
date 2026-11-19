using System;
using System.Collections.Generic;
using System.Globalization;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.UI.ViewModels;

/// <summary>
/// 驱动表中的一行（只读，来自驱动目录）
/// </summary>
public sealed class DriverItemViewModel
{
    /// <param name="parameters">驱动声明的模块参数；内置驱动与未加载的驱动没有参数</param>
    public DriverItemViewModel(DriverDescriptor driver, IReadOnlyList<ParameterInfo>? parameters = null)
    {
        Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        Parameters = parameters ?? [];
        AddressRange = string.Create(CultureInfo.InvariantCulture, $"{driver.MinAddress} – {driver.MaxAddress}");
    }

    public DriverDescriptor Driver { get; }

    public IReadOnlyList<ParameterInfo> Parameters { get; }

    public string Name => Driver.Name;

    public string CategoryName => Driver.Category.DisplayName();

    public string Description => Driver.Description;

    public string AddressRange { get; }
}

/// <summary>
/// 驱动表左侧的分类项；<see cref="Category"/> 为 null 表示"全部"
/// </summary>
public sealed record DriverCategoryItem(HardwareCategory? Category, string Name, int Count)
{
    public string Text => $"{Name}（{Count}）";
}
