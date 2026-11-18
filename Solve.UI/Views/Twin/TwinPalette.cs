using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Solve.Core.Domain.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 产品状态配色。状态色是现场约定的语义色，深浅主题下保持一致，操作员不用重新记忆。
/// 只靠颜色色弱者分不清，故障与报警在容器上另有文字计数。
/// </summary>
public static class TwinPalette
{
    private static readonly IBrush[] StateBrushes =
    [
        Brush("#8A8F98"), // 无数据：灰
        Brush("#DDE1E6"), // 空位：浅灰
        Brush("#9CC3F5"), // 待测：浅蓝
        Brush("#2F7CF6"), // 测试中：蓝
        Brush("#2DB55D"), // 合格：绿
        Brush("#E5484D"), // 不合格：红
        Brush("#F5A623"), // 报警：琥珀
        Brush("#8E44AD"), // 故障：紫
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

    public static IBrush For(ProductState state)
        => state.IsDefined() ? StateBrushes[(int)state] : StateBrushes[(int)ProductState.Unknown];

    public static IBrush TextOn(ProductState state)
        => state is ProductState.Empty or ProductState.Ready or ProductState.Alarm ? DarkText : LightText;

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));
}

/// <summary>
/// 图例中的一项
/// </summary>
public sealed record TwinLegendItem(string Text, IBrush Brush);

public static class TwinLegend
{
    /// <summary>
    /// 按严重度从高到低，最后是停用
    /// </summary>
    public static IReadOnlyList<TwinLegendItem> Items { get; } =
    [
        .. ProductStates.BySeverity.Select(state => new TwinLegendItem(state.DisplayName(), TwinPalette.For(state))),
        new TwinLegendItem("停用", TwinPalette.Disabled),
    ];
}

/// <summary>
/// 六层控件共用的转换器（静态实例，用 x:Static 引用，编译期即可检查）
/// </summary>
public static class TwinConverters
{
    public static readonly IValueConverter StateBrush =
        new FuncValueConverter<ProductState, IBrush>(TwinPalette.For);

    public static readonly IValueConverter StateForeground =
        new FuncValueConverter<ProductState, IBrush>(TwinPalette.TextOn);

    /// <summary>
    /// 容器边框：最严重的状态；没有启用的产品位时为停用色
    /// </summary>
    public static readonly IValueConverter WorstBrush =
        new FuncValueConverter<ProductState?, IBrush>(state => state is { } value ? TwinPalette.For(value) : TwinPalette.Disabled);

    /// <summary>
    /// 选中时加 2 px 外框
    /// </summary>
    public static readonly IValueConverter SelectionThickness =
        new FuncValueConverter<bool, Thickness>(selected => selected ? new Thickness(2) : new Thickness(0));
}
