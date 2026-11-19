using System;
using System.Globalization;
using Solve.Core.Domain.Drivers;

namespace Solve.UI.ViewModels;

/// <summary>
/// 驱动表中的一行（只读，来自驱动目录）
/// </summary>
public sealed class DriverItemViewModel
{
    public DriverItemViewModel(DriverDescriptor driver)
    {
        Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        AddressRange = string.Create(CultureInfo.InvariantCulture, $"{driver.MinAddress} – {driver.MaxAddress}");
    }

    public DriverDescriptor Driver { get; }

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
