using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solve.Core.Application.Devices.Runtime;
using Solve.Device.Sdk;

namespace Solve.UI.ViewModels;

/// <summary>
/// 点位读数的显示格式：名称、取自声明的单位、最多三位小数。模块表与接线面板共用，同一读数在两处显示一致。
/// </summary>
internal static class ReadingFormat
{
    /// <summary>
    /// 模块表中一个通道级点位最多列出的通道数，其余以省略号表示（完整读数在拓扑页的接线面板里按位置查看）
    /// </summary>
    private const int MaxListedChannels = 8;

    public static string Value(PointInfo point, double value)
        => $"{value.ToString("0.###", CultureInfo.InvariantCulture)}{(point.Unit.Length > 0 ? " " + point.Unit : string.Empty)}";

    /// <summary>
    /// 一个模块的全部读数，按驱动声明的顺序：模块级点位"名称 值"，通道级点位"名称 1:值 2:值 …"
    /// </summary>
    public static string Module(IReadOnlyList<PointInfo> points, IReadOnlyList<PointValue> values)
    {
        var parts = new List<string>(points.Count);
        foreach (var point in points)
        {
            if (point.Group is null)
            {
                foreach (var value in values)
                {
                    if (value.Key == point.Key && value.Channel == 0)
                        parts.Add($"{point.Name} {Value(point, value.Value)}");
                }

                continue;
            }

            var channels = values.Where(v => v.Key == point.Key && v.Channel > 0).OrderBy(v => v.Channel).ToList();
            if (channels.Count == 0)
                continue;

            var listed = string.Join(" ", channels.Take(MaxListedChannels)
                .Select(v => $"{v.Channel}:{v.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
            var more = channels.Count > MaxListedChannels ? " …" : string.Empty;
            parts.Add($"{point.Name}{(point.Unit.Length > 0 ? "(" + point.Unit + ")" : string.Empty)} {listed}{more}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 一路通道的读数：该通道组的通道级点位；组内没有通道级点位时（如单输出电源）用模块级点位
    /// </summary>
    public static string Channel(IReadOnlyList<PointInfo> points, IReadOnlyList<PointValue> values, string group, int channel)
    {
        var own = points.Where(p => p.Group == group).ToList();
        var shown = own.Count > 0 ? own : points.Where(p => p.Group is null).ToList();
        var parts = new List<string>(shown.Count);
        foreach (var point in shown)
        {
            var wanted = point.Group is null ? 0 : channel;
            foreach (var value in values)
            {
                if (value.Key == point.Key && value.Channel == wanted)
                    parts.Add($"{point.Name} {Value(point, value.Value)}");
            }
        }

        return string.Join(" · ", parts);
    }
}
