using Solve.Device.Sdk;

namespace Solve.Core.Tests.DeviceLibrary;

/// <summary>
/// SDK 契约中的校验规则：驱动作者与设备库按同一套规则判断声明是否有效
/// </summary>
public sealed class SdkContractTests
{
    private static DriverInfo Valid() => new("acme.psu-3000", "Acme PSU-3000", DeviceCategory.PowerAndLoad, "", 1, 247)
    {
        Points = [new PointInfo("voltage", "输出电压", "V", PointAccess.Read)],
        Commands = [new CommandInfo("output.on", "打开输出", CommandSafety.Output)],
        Parameters = [new ParameterInfo("channels", "通道数", ParameterKind.Integer, "4") { Minimum = 1, Maximum = 8 }],
    };

    // ---------- 驱动说明 ----------

    [Fact]
    public void ValidDriver_HasNoProblems() => Assert.Empty(Valid().Validate());

    [Theory]
    [InlineData("Acme.PSU")]
    [InlineData("")]
    [InlineData("有中文")]
    public void BadDriverId_IsReported(string id) => Assert.Single((Valid() with { Id = id }).Validate());

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 256)]
    [InlineData(10, 5)]
    public void BadAddressRange_IsReported(int min, int max)
        => Assert.Single((Valid() with { MinAddress = min, MaxAddress = max }).Validate());

    [Fact]
    public void DuplicateOrMalformedKeys_AreReported()
    {
        var driver = Valid() with
        {
            Points =
            [
                new PointInfo("voltage", "电压", "V", PointAccess.Read),
                new PointInfo("voltage", "电压 2", "V", PointAccess.Read),
                new PointInfo("Bad Key", "坏标识", "", PointAccess.Read),
            ],
        };

        Assert.Equal(2, driver.Validate().Count);
    }

    [Fact]
    public void NullLists_AreReported_NotThrown()
    {
        var driver = Valid() with { Points = null!, Commands = null!, Parameters = null! };

        Assert.Equal(3, driver.Validate().Count);
    }

    // ---------- 参数声明 ----------

    [Fact]
    public void ChoiceWithoutOptions_IsReported()
        => Assert.NotEmpty(new ParameterInfo("framing", "报文封装", ParameterKind.Choice, "rtu").ValidateDeclaration());

    [Fact]
    public void InvalidDefault_IsReported()
    {
        var parameter = new ParameterInfo("channels", "通道数", ParameterKind.Integer, "9") { Minimum = 1, Maximum = 8 };

        Assert.Contains(parameter.ValidateDeclaration(), p => p.Contains("默认值", StringComparison.Ordinal));
    }

    [Fact]
    public void MinGreaterThanMax_IsReported()
        => Assert.NotEmpty(new ParameterInfo("range", "量程", ParameterKind.Number, "1") { Minimum = 10, Maximum = 1 }.ValidateDeclaration());

    // ---------- 参数取值 ----------

    [Theory]
    [InlineData(" 4 ", "4")]
    [InlineData("1", "1")]
    [InlineData("8", "8")]
    public void Integer_IsNormalized(string raw, string expected)
        => Assert.Equal(expected, Valid().Parameters[0].Normalize(raw, out _));

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("4.5")]
    [InlineData("abc")]
    public void Integer_OutOfRangeOrMalformed_IsRejected(string raw)
    {
        Assert.Null(Valid().Parameters[0].Normalize(raw, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Number_Boolean_Choice_Text_AreNormalized()
    {
        Assert.Equal("1.5", new ParameterInfo("k", "量程", ParameterKind.Number, "0").Normalize(" 1.50 ", out _));
        Assert.Null(new ParameterInfo("k", "量程", ParameterKind.Number, "0").Normalize("NaN", out _));
        Assert.Equal("true", new ParameterInfo("k", "启用", ParameterKind.Boolean, "false").Normalize("TRUE", out _));

        var framing = new ParameterInfo("framing", "报文封装", ParameterKind.Choice, "rtu")
        {
            Choices = [new ParameterChoice("rtu", "RTU"), new ParameterChoice("tcp", "TCP")],
        };
        Assert.Equal("tcp", framing.Normalize("tcp", out _));
        Assert.Null(framing.Normalize("TCP", out _));

        var text = new ParameterInfo("tag", "标签", ParameterKind.Text, "") { MaxLength = 3 };
        Assert.Equal("abc", text.Normalize(" abc ", out _));
        Assert.Null(text.Normalize("abcd", out _));
    }

    [Fact]
    public void DeviceParameters_TypedAccess_AndUndeclaredKeyThrows()
    {
        var parameters = new DeviceParameters(new Dictionary<string, string>
        {
            ["channels"] = "4",
            ["range"] = "1.5",
            ["enabled"] = "true",
            ["framing"] = "rtu",
        });

        Assert.Equal(4, parameters.GetInteger("channels"));
        Assert.Equal(1.5, parameters.GetNumber("range"));
        Assert.True(parameters.GetBoolean("enabled"));
        Assert.Equal("rtu", parameters.GetText("framing"));
        Assert.Throws<KeyNotFoundException>(() => parameters.GetInteger("missing"));
    }

    // ---------- 时序 ----------

    [Fact]
    public void ModbusRtuGap_IsThreeAndAHalfCharacters()
        => Assert.Equal(new FrameGap(3.5, TimeSpan.FromMilliseconds(1.75)), FrameGap.ModbusRtu);

    [Fact]
    public void Failed_DefaultsToTransient()
        => Assert.Equal(FailureKind.Transient, new ExchangeStep.Failed("干扰").Kind);
}
