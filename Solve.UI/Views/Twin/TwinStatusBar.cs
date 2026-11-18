using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Solve.Core.Domain.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 状态比例条：按严重度从左到右画出各状态所占比例，停用的排在最后。
/// </summary>
/// <remarks>
/// 自绘一个元素，而不是每个分段一个控件：摘要模式下可能同时有上千条，元素数直接决定显示区的流畅度。
/// 每个非零分段至少 2 px，千分之一的故障也看得见。
/// </remarks>
public sealed class TwinStatusBar : Control
{
    public static readonly StyledProperty<TwinStatusSummary?> SummaryProperty =
        AvaloniaProperty.Register<TwinStatusBar, TwinStatusSummary?>(nameof(Summary));

    private const double MinSegmentWidth = 2;

    static TwinStatusBar()
    {
        AffectsRender<TwinStatusBar>(SummaryProperty);
    }

    public TwinStatusSummary? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using (context.PushClip(new RoundedRect(bounds, Math.Min(3, bounds.Height / 2))))
        {
            context.FillRectangle(TwinPalette.Track, bounds);
            if (Summary is not { Total: > 0 } summary)
                return;

            var x = 0.0;
            foreach (var state in ProductStates.BySeverity)
                x = DrawSegment(context, bounds, x, summary[state], summary.Total, TwinPalette.For(state));

            DrawSegment(context, bounds, x, summary.Disabled, summary.Total, TwinPalette.Disabled);
        }
    }

    private static double DrawSegment(DrawingContext context, Rect bounds, double x, int count, int total, IBrush brush)
    {
        if (count == 0 || x >= bounds.Width)
            return x;

        var width = Math.Max(MinSegmentWidth, bounds.Width * count / total);
        context.FillRectangle(brush, new Rect(x, 0, Math.Min(width, bounds.Width - x), bounds.Height));
        return x + width;
    }
}
