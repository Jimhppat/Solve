using Avalonia;
using Solve.UI.Views.Twin;

namespace Solve.Core.Tests.UI;

/// <summary>
/// 治具位自绘产品格的几何：行数、最小尺寸、格子区域（外边距、像素对齐、互不重叠）、点击位置换算
/// </summary>
public sealed class ProductGridLayoutTests
{
    private const double Epsilon = 1e-9;

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(5, 4, 2)]
    [InlineData(16, 4, 4)]
    public void Rows_RoundUp(int count, int columns, int rows)
    {
        Assert.Equal(rows, ProductGridLayout.Rows(count, columns));
    }

    [Fact]
    public void InvalidArguments_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductGridLayout.Rows(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductGridLayout.Rows(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductGridLayout.CellRect(new Size(100, 100), 4, 4, 4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductGridLayout.CellRect(new Size(100, 100), 4, 4, -1, 1));
    }

    [Fact]
    public void MinimumSize_IsMinCellTimesGrid()
    {
        Assert.Equal(default, ProductGridLayout.MinimumSize(0, 4));
        Assert.Equal(new Size(4 * ProductGridLayout.MinCellWidth, ProductGridLayout.MinCellHeight), ProductGridLayout.MinimumSize(4, 4));
        // 5 个产品、每行 4 个：两行，第二行只有 1 个，宽度仍按 4 列
        Assert.Equal(new Size(4 * ProductGridLayout.MinCellWidth, 2 * ProductGridLayout.MinCellHeight), ProductGridLayout.MinimumSize(5, 4));
    }

    [Fact]
    public void CellRect_SplitsEvenly_AndKeepsTheMargin()
    {
        var size = new Size(400, 100);

        Assert.Equal(new Rect(1, 1, 98, 48), ProductGridLayout.CellRect(size, 8, 4, 0, 1));
        Assert.Equal(new Rect(101, 51, 98, 48), ProductGridLayout.CellRect(size, 8, 4, 5, 1));
        Assert.Equal(new Rect(301, 51, 98, 48), ProductGridLayout.CellRect(size, 8, 4, 7, 1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void CellRect_SnapsInnerEdgesToDevicePixels_AndNeighboursKeepTheGap(double scale)
    {
        // 100 / 3 列、31 / 2 行除不尽，尺寸本身也不是整设备像素：
        // 内部分界线落在整设备像素上，外边就是治具的边；相邻格子之间正好 2 个外边距、不重叠
        var size = new Size(100, 31);
        const int count = 6, columns = 3;

        for (var i = 0; i < count; i++)
        {
            var cell = ProductGridLayout.CellRect(size, count, columns, i, scale);
            var column = i % columns;
            var row = i / columns;

            if (column > 0)
                AssertOnDevicePixel(cell.X - ProductGridLayout.CellMargin, scale);
            if (row > 0)
                AssertOnDevicePixel(cell.Y - ProductGridLayout.CellMargin, scale);

            if (column < columns - 1)
            {
                AssertOnDevicePixel(cell.Right + ProductGridLayout.CellMargin, scale);
                var right = ProductGridLayout.CellRect(size, count, columns, i + 1, scale);
                Assert.Equal(2 * ProductGridLayout.CellMargin, right.X - cell.Right, Epsilon);
            }
        }

        var first = ProductGridLayout.CellRect(size, count, columns, 0, scale);
        var last = ProductGridLayout.CellRect(size, count, columns, count - 1, scale);
        Assert.Equal(new Point(ProductGridLayout.CellMargin, ProductGridLayout.CellMargin), first.TopLeft);
        Assert.Equal(size.Width - ProductGridLayout.CellMargin, last.Right, Epsilon);
        Assert.Equal(size.Height - ProductGridLayout.CellMargin, last.Bottom, Epsilon);
    }

    [Fact]
    public void CellRect_TooSmall_IsEmptyNotNegative()
    {
        var cell = ProductGridLayout.CellRect(new Size(4, 1), 4, 4, 0, 1);

        Assert.Equal(0, cell.Width);
        Assert.Equal(0, cell.Height);
    }

    [Fact]
    public void IndexAt_CellCentre_RoundTrips()
    {
        var size = new Size(400, 100);
        const int count = 8, columns = 4;

        for (var i = 0; i < count; i++)
            Assert.Equal(i, ProductGridLayout.IndexAt(size, count, columns, ProductGridLayout.CellRect(size, count, columns, i, 1).Center));
    }

    [Fact]
    public void IndexAt_GapBelongsToAProduct_EmptyAndOutsideDoNot()
    {
        var size = new Size(400, 100);

        // 两格之间的缝隙算作相邻的格子
        Assert.Equal(0, ProductGridLayout.IndexAt(size, 8, 4, new Point(99.5, 25)));
        Assert.Equal(1, ProductGridLayout.IndexAt(size, 8, 4, new Point(100.5, 25)));

        // 5 个产品、每行 4 个：第二行第 2 格以后是空位
        Assert.Equal(4, ProductGridLayout.IndexAt(size, 5, 4, new Point(50, 75)));
        Assert.Equal(-1, ProductGridLayout.IndexAt(size, 5, 4, new Point(150, 75)));

        Assert.Equal(-1, ProductGridLayout.IndexAt(size, 8, 4, new Point(-1, 25)));
        Assert.Equal(-1, ProductGridLayout.IndexAt(size, 8, 4, new Point(400, 25)));
        Assert.Equal(-1, ProductGridLayout.IndexAt(size, 8, 4, new Point(50, 100)));
        Assert.Equal(-1, ProductGridLayout.IndexAt(default, 8, 4, new Point(0, 0)));
        Assert.Equal(-1, ProductGridLayout.IndexAt(size, 0, 4, new Point(50, 25)));
    }

    private static void AssertOnDevicePixel(double value, double scale)
    {
        var pixels = value * scale;
        Assert.Equal(Math.Round(pixels), pixels, 1e-6);
    }
}
