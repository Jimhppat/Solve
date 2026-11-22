namespace GJ.Drivers.Da750;

/// <summary>
/// DA750 系列一个型号的额定值与读数倍率。同系列其他型号（如 DA750-4-I1、DA1050-4）帧格式相同、倍率不同，
/// 确认倍率后新增一个实例并在驱动包入口登记即可。
/// </summary>
/// <param name="Model">型号，如 "DA750-4-60A"</param>
/// <param name="Channels">负载通道数</param>
/// <param name="RatedVoltage">单通道额定电压，V</param>
/// <param name="RatedCurrent">单通道额定电流，A</param>
/// <param name="RatedPower">单通道额定功率，W</param>
/// <param name="MinCurrent">CC 模式最小设定电流，A</param>
/// <param name="MinCvVoltage">CV 模式最小设定电压，V</param>
/// <param name="CurrentScale">电流读数与 CC 设定值的倍率：原始值 = 电流（A）× 倍率</param>
internal sealed record Da750Model(
    string Model,
    int Channels,
    double RatedVoltage,
    double RatedCurrent,
    double RatedPower,
    double MinCurrent,
    double MinCvVoltage,
    double CurrentScale)
{
    /// <summary>
    /// DA750-4-60A：4 通道，单通道 60 V / 60 A / 750 W。
    /// 额定值与最小值取自旧驱动 GJ.DEV/Load/DA750_4_60A.cs；电流倍率 ×100（0.01 A / LSB）：
    /// 60 A × 1000 会超出 16 位，旧驱动对 60 A 机型的读数与设定都用 ×100（5 A 的 LED 机型才用 ×1000）。
    /// </summary>
    public static Da750Model Da750_4_60A { get; } = new("DA750-4-60A", 4, 60, 60, 750, 0.1, 5, 100);

    /// <summary>
    /// 驱动标识：型号转小写，如 "gj.da750-4-60a"。一经发布不得改名。
    /// </summary>
    public string DriverId => "gj." + Model.ToLowerInvariant();
}
