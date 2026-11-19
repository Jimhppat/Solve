using System;
using Avalonia;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 治具位里产品格的几何：按列数等分的网格（与原先的 UniformGrid 一致），每格外边距 1 px，格与格之间留 2 px。
/// 纯计算、不依赖控件，自绘的治具位用它排版和换算点击位置。
/// </summary>
public static class ProductGridLayout
{
    /// <summary>
    /// 最小格宽（含外边距）：两位序号刚好放得下；再小就让显示区出现滚动条，而不是继续压缩
    /// </summary>
    public const double MinCellWidth = 20;

    /// <summary>
    /// 最小格高（含外边距）
    /// </summary>
    public const double MinCellHeight = 18;

    public const double CellMargin = 1;

    public static int Rows(int count, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        return (count + columns - 1) / columns;
    }

    /// <summary>
    /// 放得下全部产品格的最小尺寸；没有产品格时为 0
    /// </summary>
    public static Size MinimumSize(int count, int columns)
    {
        var rows = Rows(count, columns);
        return rows == 0 ? default : new Size(columns * MinCellWidth, rows * MinCellHeight);
    }

    /// <summary>
    /// 第 <paramref name="index"/> 个产品格的区域（已扣除外边距）。格子边缘对齐到设备像素，
    /// 等分出的小数坐标不会让格子边缘发虚、相邻格子的缝隙忽宽忽窄。
    /// </summary>
    /// <param name="scale">界面缩放（设备像素 / 逻辑像素）；不大于 0 时不对齐</param>
    public static Rect CellRect(Size size, int count, int columns, int index, double scale)
    {
        var rows = Rows(count, columns);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        var column = index % columns;
        var row = index / columns;
        var left = Edge(size.Width, column, columns, scale);
        var right = Edge(size.Width, column + 1, columns, scale);
        var top = Edge(size.Height, row, rows, scale);
        var bottom = Edge(size.Height, row + 1, rows, scale);

        var width = Math.Max(0, right - left - 2 * CellMargin);
        var height = Math.Max(0, bottom - top - 2 * CellMargin);
        return new Rect(left + CellMargin, top + CellMargin, width, height);
    }

    /// <summary>
    /// 点所在的产品格序号；点在区域外、或落在末行没有产品的空位上时为 -1。
    /// 格与格之间 2 px 的缝隙算作所在的格子：点偏一点也能选中，不会"点了没反应"。
    /// </summary>
    public static int IndexAt(Size size, int count, int columns, Point point)
    {
        var rows = Rows(count, columns);
        if (rows == 0 || size.Width <= 0 || size.Height <= 0)
            return -1;

        if (point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height)
            return -1;

        var column = Math.Min(columns - 1, (int)(point.X * columns / size.Width));
        var row = Math.Min(rows - 1, (int)(point.Y * rows / size.Height));
        var index = row * columns + column;
        return index < count ? index : -1;
    }

    /// <summary>
    /// 第 i 条分界线（共 n 格）。内部分界线对齐到设备像素；两端就是治具的边，不对齐，
    /// 尺寸本身不是整设备像素时最后一格也不会画出界
    /// </summary>
    private static double Edge(double length, int i, int n, double scale)
    {
        if (i <= 0)
            return 0;

        if (i >= n)
            return length;

        var value = length * i / n;
        return scale > 0 ? Math.Round(value * scale) / scale : value;
    }
}
