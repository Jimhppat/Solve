namespace Chroma.Drivers.Psu62000B;

/// <summary>
/// 62000B 系列一个型号的额定值。同系列其他型号命令集相同，只需新增一个实例并在驱动包入口登记。
/// </summary>
/// <param name="Model">型号，与 *IDN? 返回的型号字段一致，如 "62015B-15-90"</param>
/// <param name="RatedVoltage">额定输出电压，V</param>
/// <param name="RatedCurrent">额定输出电流，A</param>
/// <param name="RatedPower">额定输出功率，W</param>
internal sealed record Chroma62000BModel(string Model, double RatedVoltage, double RatedCurrent, double RatedPower)
{
    /// <summary>
    /// 62015B-15-90：15 V / 90 A / 1350 W（62000B 操作手册 v1.5 规格表）
    /// </summary>
    public static Chroma62000BModel M62015B_15_90 { get; } = new("62015B-15-90", 15, 90, 1350);

    /// <summary>
    /// 驱动标识：型号转小写，如 "chroma.62015b-15-90"。一经发布不得改名。
    /// </summary>
    public string DriverId => "chroma." + Model.ToLowerInvariant();
}
