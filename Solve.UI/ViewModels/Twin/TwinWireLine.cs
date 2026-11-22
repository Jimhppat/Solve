using Solve.Core.Application.Wiring;
using Solve.Core.Domain.Drivers;

namespace Solve.UI.ViewModels.Twin;

/// <summary>
/// 产品信息卡片中的一路接线：如"供电 · 电源-01 输出 · 就绪 · 输出电压 12 V"。值相等即内容相同，刷新时据此判断是否需要重绘。
/// </summary>
/// <param name="Text">一行说明（不含系统 Id）</param>
/// <param name="HealthColor">健康度的颜色</param>
public sealed record TwinWireLine(string Text, string HealthColor)
{
    public static TwinWireLine From(WireStatus status)
    {
        var readings = status is { Driver: { } driver, Module: { } module }
            ? ReadingFormat.Channel(driver.Points, module.Points, status.Wire.Channel.Group, status.Wire.Channel.Index)
            : string.Empty;
        var parts = new List<string> { status.Wire.Kind.DisplayName(), $"{status.ModuleText} {status.ChannelText}", status.Health.DisplayName() };
        if (readings.Length > 0)
            parts.Add(readings);

        var color = status.Health switch
        {
            WireHealth.Ready => "#10B981",
            WireHealth.Waiting or WireHealth.NoProtocol => "#94A3B8",
            _ => "#EF4444",
        };
        return new TwinWireLine(string.Join(" · ", parts), color);
    }
}
