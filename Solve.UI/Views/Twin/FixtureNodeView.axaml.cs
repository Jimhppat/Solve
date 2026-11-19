using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Solve.UI.ViewModels.Twin;

namespace Solve.UI.Views.Twin;

/// <summary>
/// 治具位：一个自绘元素画出它的全部产品格（展开模式），或小号编号加状态比例条（摘要模式）。
/// </summary>
/// <remarks>
/// <para>为什么自绘：产品位是显示区里数量最多的节点。原先每个产品位一个控件（7 个可视元素、8 个绑定），
/// 2 柜 × 10 层 × 4 槽 × 4 治具 × 4 产品时切换一次柜体要创建、匹配样式、布局约 7300 个元素，界面卡住 0.4～0.6 s；
/// 一个治具一个元素后，产品位不再产生任何控件。</para>
/// <para>状态变化：挂到界面上时订阅本治具及其产品位视图模型的属性通知，产品状态或选中变化时只重画本治具；
/// 离开界面即退订，切走的柜体不会因为事件订阅留在内存里。</para>
/// <para>点选：按点击位置换算出产品格，冒泡 <see cref="ProductTappedEvent"/>（带格子区域），由显示区把产品信息卡片挂在格子旁并选中。
/// 只在 UI 线程上使用。</para>
/// </remarks>
public partial class FixtureNodeView : Control
{
    // 产品格：圆角 3、选中时 2 px 外框。格子里只画序号（11 号半粗），状态只用格子颜色表示：
    // 状态名在小格子里放不下，大小格子显示的内容不一致反而难认；具体状态名点选后在产品信息卡片里查看
    private const double CellCornerRadius = 3;
    private const double SelectionThickness = 2;
    private const double OrdinalFontSize = 11;

    // 摘要模式：小号编号 + 状态条，与槽位摘要的样式一致
    private const double SummaryPadding = 3;
    private const double SummarySpacing = 3;
    private const double SummaryFontSize = 10;
    private const double SummaryBarHeight = 8;
    private const double SummaryBarMinWidth = 32;

    // 序号与编号的排版结果按文字、字形、字号、颜色缓存：上千个产品格只有几十种不同的文字，重画时不必重新排版。
    // 只在 UI 线程上使用；超过上限（字体或主题颜色反复变化）时整体释放重建，不会无限增长
    private const int TextCacheLimit = 512;
    private static readonly Dictionary<(string Text, Typeface Face, double Size, IBrush Brush), TextLayout> TextCache = [];

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private FixtureNodeViewModel? _fixture;
    private FixtureNodeViewModel? _subscribedFixture;
    private IReadOnlyList<TwinNodeViewModel> _subscribedProducts = [];
    private bool _attached;

    public FixtureNodeView()
    {
        InitializeComponent();

        // 订阅自身的事件，随控件一起回收
        Tapped += OnTapped;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual(); // 选中框与摘要文字用主题色
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unsubscribe();
        _fixture = DataContext as FixtureNodeViewModel;
        if (_attached)
            Subscribe();

        ApplyMode();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_fixture is not { } fixture)
            return default;

        if (fixture.IsExpanded)
            return ProductGridLayout.MinimumSize(fixture.ChildCount, fixture.ProductColumns);

        var code = Text(fixture.ShortCode, FaceOf(FontWeight.Normal), SummaryFontSize, SummaryTextBrush());
        return new Size(
            Math.Max(code.Width, SummaryBarMinWidth) + 2 * SummaryPadding,
            code.Height + SummarySpacing + SummaryBarHeight + 2 * SummaryPadding);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (_fixture is not { } fixture || size.Width <= 0 || size.Height <= 0)
            return;

        // 透明底：自绘元素只在画到的地方命中，铺一层透明底让整个治具区域都能接收点击与悬停提示
        context.FillRectangle(Brushes.Transparent, new Rect(size));

        if (fixture.IsExpanded)
            RenderProducts(context, fixture, size);
        else
            RenderSummary(context, fixture, size);
    }

    // ==================== 绘制 ====================

    private void RenderProducts(DrawingContext context, FixtureNodeViewModel fixture, Size size)
    {
        var products = fixture.Children;
        var columns = fixture.ProductColumns;
        var scale = LayoutHelper.GetLayoutScale(this);
        var ordinalFace = FaceOf(FontWeight.SemiBold);
        IPen? selectionPen = null;

        for (var i = 0; i < products.Count; i++)
        {
            if (products[i] is not ProductNodeViewModel product)
                continue;

            var cell = ProductGridLayout.CellRect(size, products.Count, columns, i, scale);
            if (cell.Width <= 0 || cell.Height <= 0)
                continue;

            // 自身被停用的产品格变淡；上级停用时由上级整体变淡（与其他各层一致）
            if (product.Node.IsEnabled)
            {
                DrawCell(context, cell, product, ordinalFace, ref selectionPen);
            }
            else
            {
                using (context.PushOpacity(TwinNodeViewModel.DisabledOpacity))
                    DrawCell(context, cell, product, ordinalFace, ref selectionPen);
            }
        }
    }

    private void DrawCell(DrawingContext context, Rect cell, ProductNodeViewModel product, Typeface ordinalFace, ref IPen? selectionPen)
    {
        context.DrawRectangle(TwinPalette.For(product.State), null, cell, CellCornerRadius, CellCornerRadius);

        if (product.IsSelected)
        {
            selectionPen ??= new Pen(ResourceBrush("SemiColorText0", Brushes.Black), SelectionThickness);
            const double inset = SelectionThickness / 2;
            context.DrawRectangle(null, selectionPen, cell.Deflate(inset), CellCornerRadius - inset, CellCornerRadius - inset);
        }

        // 序号居中；深色格子上用白字、浅色格子上用深字
        var ordinal = Text(product.OrdinalText, ordinalFace, OrdinalFontSize, TwinPalette.TextOn(product.State));
        var origin = new Point(cell.X + (cell.Width - ordinal.Width) / 2, cell.Y + (cell.Height - ordinal.Height) / 2);

        // 只有序号比格子还大时才裁剪，平时不多加绘制指令
        if (ordinal.Width > cell.Width || ordinal.Height > cell.Height)
        {
            using (context.PushClip(cell))
                ordinal.Draw(context, origin);
        }
        else
        {
            ordinal.Draw(context, origin);
        }
    }

    private void RenderSummary(DrawingContext context, FixtureNodeViewModel fixture, Size size)
    {
        var code = Text(fixture.ShortCode, FaceOf(FontWeight.Normal), SummaryFontSize, SummaryTextBrush());
        var contentHeight = code.Height + SummarySpacing + SummaryBarHeight;
        var top = Math.Max(SummaryPadding, (size.Height - contentHeight) / 2);

        code.Draw(context, new Point(SummaryPadding, top));
        var bar = new Rect(SummaryPadding, top + code.Height + SummarySpacing, Math.Max(0, size.Width - 2 * SummaryPadding), SummaryBarHeight);
        TwinStatusBar.Draw(context, bar, fixture.Summary);
    }

    private Typeface FaceOf(FontWeight weight) => new(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight);

    private IBrush SummaryTextBrush() => ResourceBrush("SemiColorText2", Brushes.Gray);

    private IBrush ResourceBrush(string key, IBrush fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static TextLayout Text(string text, Typeface face, double size, IBrush brush)
    {
        var key = (text, face, size, brush);
        if (TextCache.TryGetValue(key, out var layout))
            return layout;

        if (TextCache.Count >= TextCacheLimit)
        {
            // 已画出的帧持有字形的独立引用，这里释放不影响画面
            foreach (var cached in TextCache.Values)
                cached.Dispose();
            TextCache.Clear();
        }

        layout = new TextLayout(text, face, size, brush);
        TextCache.Add(key, layout);
        return layout;
    }

    // ==================== 视图模型 ====================

    private void ApplyMode()
    {
        var fixture = _fixture;
        Opacity = fixture?.ContentOpacity ?? 1;
        if (fixture is { IsExpanded: true })
            Cursor = HandCursor;
        else
            ClearValue(CursorProperty);

        // 摘要模式没有产品格可点，悬停显示全部计数（与槽位摘要一致）；展开模式不放悬停提示
        ToolTip.SetTip(this, fixture is { IsSummary: true } ? fixture.SummaryText : null);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Subscribe()
    {
        if (_fixture is not { } fixture || _subscribedFixture is not null)
            return;

        fixture.PropertyChanged += OnFixtureChanged;
        _subscribedFixture = fixture;
        _subscribedProducts = fixture.Children;
        foreach (var product in _subscribedProducts)
            product.PropertyChanged += OnProductChanged;

        // 不在界面上期间收不到通知，挂上时按最新值重画
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_subscribedFixture is not { } fixture)
            return;

        fixture.PropertyChanged -= OnFixtureChanged;
        foreach (var product in _subscribedProducts)
            product.PropertyChanged -= OnProductChanged;

        _subscribedFixture = null;
        _subscribedProducts = [];
    }

    private void OnFixtureChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 展开、收起会替换下级列表：改订新的产品位
            case nameof(TwinNodeViewModel.Children):
                Unsubscribe();
                Subscribe();
                ApplyMode();
                break;

            // 统计变化：只有摘要模式画在治具上（展开模式的变化来自各产品位自己的通知）
            case nameof(TwinNodeViewModel.SummaryText) when _fixture is { IsSummary: true } fixture:
                ToolTip.SetTip(this, fixture.SummaryText);
                InvalidateVisual();
                break;
        }
    }

    private void OnProductChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProductNodeViewModel.State) or nameof(ProductNodeViewModel.IsSelected))
            InvalidateVisual();
    }

    /// <summary>
    /// 点（本元素坐标）所在的产品格对应的产品位；摘要模式、点在区域外或末行空位上时为 null
    /// </summary>
    public ProductNodeViewModel? ProductAt(Point point)
    {
        if (_fixture is not { IsExpanded: true } fixture)
            return null;

        var index = ProductGridLayout.IndexAt(Bounds.Size, fixture.ChildCount, fixture.ProductColumns, point);
        return index >= 0 ? fixture.Children[index] as ProductNodeViewModel : null;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_fixture is not { IsExpanded: true } fixture)
            return;

        var index = ProductGridLayout.IndexAt(Bounds.Size, fixture.ChildCount, fixture.ProductColumns, e.GetPosition(this));
        if (index < 0 || fixture.Children[index] is not ProductNodeViewModel product)
            return;

        // 先让上级（显示区）知道点的是哪一格、格子在哪里，好把产品信息卡片挂在这一格旁边；没有上级处理时直接选中
        var cell = ProductGridLayout.CellRect(Bounds.Size, fixture.ChildCount, fixture.ProductColumns, index, LayoutHelper.GetLayoutScale(this));
        var tapped = new ProductTappedEventArgs(ProductTappedEvent, product, cell);
        RaiseEvent(tapped);
        if (!tapped.Handled)
            product.Select();

        e.Handled = true;
    }

    /// <summary>
    /// 点选了一个产品格（冒泡）。<see cref="ProductTappedEventArgs.Cell"/> 是格子在本元素中的区域
    /// </summary>
    public static readonly RoutedEvent<ProductTappedEventArgs> ProductTappedEvent =
        RoutedEvent.Register<FixtureNodeView, ProductTappedEventArgs>(nameof(ProductTapped), RoutingStrategies.Bubble);

    public event EventHandler<ProductTappedEventArgs>? ProductTapped
    {
        add => AddHandler(ProductTappedEvent, value);
        remove => RemoveHandler(ProductTappedEvent, value);
    }
}

/// <summary>
/// 点选产品格：哪个产品位、格子在治具元素中的区域（用于把产品信息卡片挂在格子旁边）
/// </summary>
public sealed class ProductTappedEventArgs(RoutedEvent routedEvent, ProductNodeViewModel product, Rect cell) : RoutedEventArgs(routedEvent)
{
    public ProductNodeViewModel Product { get; } = product;

    public Rect Cell { get; } = cell;
}
