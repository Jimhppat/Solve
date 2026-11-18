using Solve.Core.Domain.Topology;

namespace Solve.Core.Tests.Topology;

public sealed class TopologyAddressTests
{
    // ---------- 构造与层级 ----------

    [Fact]
    public void Default_IsEmpty_AndNotSystem()
    {
        var empty = default(TopologyAddress);

        Assert.True(empty.IsEmpty);
        Assert.NotEqual(TopologyAddress.System, empty);
        Assert.False(TopologyAddress.System.IsEmpty);
        Assert.Equal(TopologyLevel.System, TopologyAddress.System.Level);
    }

    [Fact]
    public void Of_DepthDeterminesLevel()
    {
        Assert.Equal(TopologyAddress.System, TopologyAddress.Of());
        Assert.Equal(TopologyLevel.Cabinet, TopologyAddress.Of(1).Level);
        Assert.Equal(TopologyLevel.Layer, TopologyAddress.Of(1, 2).Level);
        Assert.Equal(TopologyLevel.Slot, TopologyAddress.Of(1, 2, 3).Level);
        Assert.Equal(TopologyLevel.Fixture, TopologyAddress.Of(1, 2, 3, 4).Level);
        Assert.Equal(TopologyLevel.Product, TopologyAddress.Of(1, 2, 3, 4, 5).Level);
    }

    [Fact]
    public void Of_ExposesEveryOrdinal()
    {
        var address = TopologyAddress.Of(1, 2, 3, 4, 5);

        Assert.Equal((1, 2, 3, 4, 5), (address.Cabinet, address.Layer, address.Slot, address.Fixture, address.Product));
        Assert.Equal(5, address.Ordinal);
        Assert.Equal(3, address.GetOrdinal(TopologyLevel.Slot));
        Assert.Equal(0, TopologyAddress.Of(1).GetOrdinal(TopologyLevel.Layer));
    }

    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 1, TopologyAddress.MaxOrdinal + 1 })]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6 })]
    public void TryCreate_InvalidOrdinals_Fails(int[] ordinals)
    {
        Assert.False(TopologyAddress.TryCreate(ordinals, out var address));
        Assert.True(address.IsEmpty);
        Assert.Throws<ArgumentException>(() => TopologyAddress.Of(ordinals));
    }

    // ---------- 父子导航 ----------

    [Fact]
    public void Child_And_Parent_AreInverse()
    {
        var product = TopologyAddress.System.Child(1).Child(2).Child(3).Child(4).Child(5);

        Assert.Equal(TopologyAddress.Of(1, 2, 3, 4, 5), product);
        Assert.True(product.TryGetParent(out var fixture));
        Assert.Equal(TopologyAddress.Of(1, 2, 3, 4), fixture);
    }

    [Fact]
    public void System_HasNoParent_ProductHasNoChild()
    {
        Assert.False(TopologyAddress.System.TryGetParent(out _));
        Assert.False(TopologyAddress.Of(1, 1, 1, 1, 1).TryGetChild(1, out _));
        Assert.Throws<InvalidOperationException>(() => TopologyAddress.Of(1, 1, 1, 1, 1).Child(1));
        Assert.Throws<InvalidOperationException>(() => default(TopologyAddress).Child(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyAddress.System.Child(0));
    }

    [Fact]
    public void AncestorAt_TruncatesToLevel()
    {
        var product = TopologyAddress.Of(2, 3, 4, 1, 8);

        Assert.Equal(TopologyAddress.Of(2), product.AncestorAt(TopologyLevel.Cabinet));
        Assert.Equal(TopologyAddress.Of(2, 3, 4), product.AncestorAt(TopologyLevel.Slot));
        Assert.Equal(TopologyAddress.System, product.AncestorAt(TopologyLevel.System));
        Assert.Equal(product, product.AncestorAt(TopologyLevel.Product));
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyAddress.Of(2).AncestorAt(TopologyLevel.Layer));
    }

    [Fact]
    public void AncestorRelations()
    {
        var slot = TopologyAddress.Of(1, 2, 3);
        var product = TopologyAddress.Of(1, 2, 3, 1, 1);
        var otherSlotProduct = TopologyAddress.Of(1, 2, 4, 1, 1);

        Assert.True(slot.IsAncestorOf(product));
        Assert.False(slot.IsAncestorOf(slot));
        Assert.True(slot.Contains(slot));
        Assert.False(slot.Contains(otherSlotProduct));
        Assert.True(TopologyAddress.System.Contains(product));
        Assert.False(default(TopologyAddress).Contains(product));
        Assert.False(slot.Contains(default));
    }

    [Fact]
    public void Ordering_IsPreOrder()
    {
        TopologyAddress[] expected =
        [
            default,
            TopologyAddress.System,
            TopologyAddress.Of(1),
            TopologyAddress.Of(1, 1),
            TopologyAddress.Of(1, 1, 2),
            TopologyAddress.Of(1, 2),
            TopologyAddress.Of(2),
            TopologyAddress.Of(10),
        ];

        var shuffled = Enumerable.Reverse(expected).ToList();
        shuffled.Sort();

        Assert.Equal(expected, shuffled);
    }

    // ---------- 文本格式 ----------

    [Theory]
    [InlineData(new int[0], "SYS")]
    [InlineData(new[] { 1 }, "C01")]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, "C01-L02-S03-F04-P05")]
    [InlineData(new[] { 12, 100, 999 }, "C12-L100-S999")]
    public void ToString_IsCanonical_AndRoundTrips(int[] ordinals, string text)
    {
        var address = TopologyAddress.Of(ordinals);

        Assert.Equal(text, address.ToString());
        Assert.Equal(address, TopologyAddress.Parse(text));
    }

    [Theory]
    [InlineData("sys", new int[0])]
    [InlineData("  c1-l2-s3  ", new[] { 1, 2, 3 })]
    [InlineData("C001-L02", new[] { 1, 2 })]
    public void TryParse_AcceptsLenientForms(string text, int[] ordinals)
    {
        Assert.True(TopologyAddress.TryParse(text, out var address));
        Assert.Equal(TopologyAddress.Of(ordinals), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(empty)")]
    [InlineData("C")]
    [InlineData("C0")]
    [InlineData("C1000")]
    [InlineData("C+1")]
    [InlineData("C 1")]
    [InlineData("L01")]
    [InlineData("C01-S01")]
    [InlineData("C01--L02")]
    [InlineData("C01-L02-")]
    [InlineData("C01-L02-S03-F04-P05-P06")]
    [InlineData("SYS-C01")]
    [InlineData("C99999999999")]
    public void TryParse_RejectsMalformedText(string? text)
    {
        Assert.False(TopologyAddress.TryParse(text, out var address));
        Assert.True(address.IsEmpty);
    }

    [Fact]
    public void Parse_Malformed_Throws()
        => Assert.Throws<FormatException>(() => TopologyAddress.Parse("C01-X02"));
}
