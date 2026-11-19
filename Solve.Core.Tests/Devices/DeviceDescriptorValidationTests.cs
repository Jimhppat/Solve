using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;

namespace Solve.Core.Tests.Devices;

public sealed class DeviceDescriptorValidationTests
{
    private static readonly DriverId Generic = DriverId.Of("generic.module");

    private static DeviceDescriptor Valid() => new(DeviceId.New(), BusId.New(), Generic, "程控电源", 1);

    private static IReadOnlyList<string> Codes(DeviceDescriptor device)
        => device.Validate().Select(e => e.Code).ToList();

    private static void AssertSingleError(DeviceDescriptor device, string field)
        => Assert.Equal([DeviceErrors.DescriptorCodePrefix + field], Codes(device));

    [Fact]
    public void Valid_HasNoErrors()
    {
        Assert.Empty(Valid().Validate());
        Assert.Empty((Valid() with { Remark = new string('r', DeviceDescriptor.MaxRemarkLength) }).Validate());
    }

    [Theory]
    [InlineData(DeviceDescriptor.MinAddress)]
    [InlineData(DeviceDescriptor.MaxAddress)]
    public void Address_AtBounds_IsValid(int address)
        => Assert.Empty((Valid() with { Address = address }).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DeviceDescriptor.MaxAddress + 1)]
    public void Address_OutOfRange_IsRejected(int address)
        => AssertSingleError(Valid() with { Address = address }, "Address");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Name_Blank_IsRejected(string? name)
        => AssertSingleError(Valid() with { Name = name! }, "Name");

    [Fact]
    public void Name_TooLong_IsRejected()
        => AssertSingleError(Valid() with { Name = new string('n', DeviceDescriptor.MaxNameLength + 1) }, "Name");

    [Fact]
    public void Remark_TooLong_IsRejected()
        => AssertSingleError(Valid() with { Remark = new string('r', DeviceDescriptor.MaxRemarkLength + 1) }, "Remark");

    [Fact]
    public void EmptyIds_AreRejected_AllErrorsAtOnce()
    {
        var device = new DeviceDescriptor(DeviceId.Empty, BusId.Empty, default, "", 0);

        Assert.Equal(
            [
                DeviceErrors.DescriptorCodePrefix + "Id",
                DeviceErrors.DescriptorCodePrefix + "BusId",
                DeviceErrors.DescriptorCodePrefix + "Driver",
                DeviceErrors.DescriptorCodePrefix + "Name",
                DeviceErrors.DescriptorCodePrefix + "Address",
            ],
            Codes(device));
    }

    [Fact]
    public void DeviceId_TryParse_RejectsEmptyAndGarbage()
    {
        Assert.False(DeviceId.TryParse(Guid.Empty.ToString(), out _));
        Assert.False(DeviceId.TryParse("not-a-guid", out _));
        Assert.False(DeviceId.TryParse(null, out _));

        var id = DeviceId.New();
        Assert.True(DeviceId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
    }

    // ---------- 模块参数 ----------

    private static ParameterValues Values(params (string Key, string Value)[] values)
        => new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)));

    [Fact]
    public void ParameterValues_CompareByContent_RegardlessOfOrder()
    {
        var a = Values(("b", "2"), ("a", "1"));
        var b = Values(("a", "1"), ("b", "2"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Values(("a", "1")));

        // 模块描述的相等性随之按参数内容比较（重新读取的模块与原模块相等）
        var device = Valid() with { Parameters = a };
        Assert.Equal(device, device with { Parameters = b });
    }

    [Fact]
    public void Parameters_BadKeyOrTooLongValue_IsRejected()
    {
        AssertSingleError(Valid() with { Parameters = Values(("Bad Key", "1")) }, "Parameters");
        AssertSingleError(Valid() with { Parameters = Values(("k", new string('v', ParameterValues.MaxValueLength + 1))) }, "Parameters");
    }

    [Fact]
    public void Parameters_Null_IsRejected_NotThrown()
        => AssertSingleError(Valid() with { Parameters = null! }, "Parameters");
}
