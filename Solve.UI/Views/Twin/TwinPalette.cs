using System.Collections.Generic;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Solve.Core.Domain.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 产品状态配色。状态色是现场约定的语义色，深浅主题下保持一致，操作员不用重新记忆。
/// 产品格只用颜色表示状态（格子里只有序号）：色弱者分不清颜色时，层号栏与柜体胶囊上另有不良品红字计数，
/// 点选产品格后控制区显示状态名。
/// </summary>
public static class TwinPalette
{
    // 按 ProductState 的数值排列
    private static readonly IBrush[] StateBrushes =
    [
        Brush("#DDE1E6"), // 空闲：浅灰（默认状态，整屏都是它时也不刺眼）
        Brush("#2F7CF6"), // 待机：蓝
        Brush("#2DB55D"), // 良品：绿
        Brush("#E5484D"), // 不良品：红
    ];

    // 浅色底上用深字，深色底上用白字
    private static readonly IBrush DarkText = Brush("#1F2329");
    private static readonly IBrush LightText = Brush("#FFFFFF");

    /// <summary>
    /// 停用 / 没有启用的产品位
    /// </summary>
    public static IBrush Disabled { get; } = Brush("#B8BCC4");

    /// <summary>
    /// 状态条的底色
    /// </summary>
    public static IBrush Track { get; } = Brush("#33888780");

    /// <summary>
    /// 容器（柜体边框、层位与胶囊的圆点）在"全部空闲"时用的中性灰：空闲格子的浅灰画成边框和圆点几乎看不见
    /// </summary>
    public static IBrush IdleOutline { get; } = Brush("#9AA0A8");

    /// <summary>
    /// 未定义的数值（数据源映射有误）按停用色画，不冒充任何一种状态
    /// </summary>
    public static IBrush For(ProductState state)
        => state.IsDefined() ? StateBrushes[(int)state] : Disabled;

    /// <summary>
    /// 容器的颜色：最严重的状态；全部空闲时为中性灰；没有启用的产品位时为停用色
    /// </summary>
    public static IBrush ForContainer(ProductState? worst) => worst switch
    {
        null => Disabled,
        ProductState.Idle => IdleOutline,
        { } state => For(state),
    };

    public static IBrush TextOn(ProductState state)
        => state is ProductState.Idle || !state.IsDefined() ? DarkText : LightText;

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));
}

/// <summary>
/// 图例中的一项
/// </summary>
public sealed record TwinLegendItem(string Text, IBrush Brush);

public static class TwinLegend
{
    /// <summary>
    /// 空闲、待机、良品、不良品，最后是停用
    /// </summary>
    public static IReadOnlyList<TwinLegendItem> Items { get; } =
    [
        .. ProductStates.All.Select(state => new TwinLegendItem(state.DisplayName(), TwinPalette.For(state))),
        new TwinLegendItem("停用", TwinPalette.Disabled),
    ];
}

/// <summary>
/// 六层控件共用的转换器（静态实例，用 x:Static 引用，编译期即可检查）
/// </summary>
public static class TwinConverters
{
    /// <summary>
    /// 容器边框与圆点：最严重的状态；全部空闲时为中性灰；没有启用的产品位时为停用色
    /// </summary>
    public static readonly IValueConverter WorstBrush =
        new FuncValueConverter<ProductState?, IBrush>(TwinPalette.ForContainer);
}
