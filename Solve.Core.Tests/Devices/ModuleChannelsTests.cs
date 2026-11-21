using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.Devices;

/// <summary>
/// 模块实例的通道配置：驱动声明通道能力，模块选择启用哪几路、起别名；通用模块手工定义通道
/// </summary>
public sealed class ModuleChannelsTests
{
    private static readonly DriverDescriptor RelayBoard = new(DriverId.Of("acme.relay-16"), "16 路继电器板", HardwareCategory.General, "", 1, 32)
    {
        Channels = [new ChannelGroup("relay", "继电器", ChannelKind.Switch, 16)],
    };

    private static readonly DriverDescriptor Generic = new(DriverId.Of("generic.module"), "通用模块", HardwareCategory.General, "", 1, 255)
    {
        AllowsManualChannels = true,
    };

    [Fact]
    public void EmptyConfig_EnablesEveryDeclaredChannel()
    {
        var group = Assert.Single(ModuleChannels.Resolve(RelayBoard, ModuleChannelConfig.Empty).Value);

        Assert.Equal(16, group.EnabledCount);
        Assert.True(group.IsEnabled(16));
        Assert.Equal("继电器 3", group.ChannelName(3));
    }

    [Fact]
    public void Subset_And_Aliases_AreApplied()
    {
        var config = new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [6, 1, 2, 2], aliases: [new(2, " 3# 压合气缸 "), new(5, "  ")])]);

        var group = Assert.Single(ModuleChannels.Resolve(RelayBoard, config).Value);

        Assert.Equal([1, 2, 6], group.Enabled());
        Assert.False(group.IsEnabled(3));
        Assert.Equal("3# 压合气缸", group.ChannelName(2));
        Assert.Equal("继电器 5", group.ChannelName(5));
    }

    [Theory]
    [InlineData("missing", null, null)]
    [InlineData("relay", "改名", null)]
    [InlineData("relay", null, 8)]
    public void DeclaredGroups_CannotBeInventedOrReshaped(string key, string? name, int? count)
    {
        var config = new ModuleChannelConfig([new ChannelGroupSetting(key, name, count: count)]);

        Assert.True(ModuleChannels.Resolve(RelayBoard, config).IsFailure);
    }

    [Fact]
    public void EnabledChannelBeyondDeclaredCount_IsRejected()
        => Assert.True(ModuleChannels.Resolve(RelayBoard, new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [17])])).IsFailure);

    [Fact]
    public void GenericModule_DefinesItsOwnGroups()
    {
        var config = new ModuleChannelConfig(
        [
            new ChannelGroupSetting("g1", "继电器", ChannelKind.Switch, 8, enabled: [1, 2, 3]),
            new ChannelGroupSetting("g2", "在位", ChannelKind.Detect, 4),
        ]);

        var groups = ModuleChannels.Resolve(Generic, config).Value;

        Assert.Equal([("继电器", ChannelKind.Switch, 3), ("在位", ChannelKind.Detect, 4)],
            groups.Select(g => (g.Name, g.Kind, g.EnabledCount)));
        Assert.Empty(ModuleChannels.Resolve(Generic, ModuleChannelConfig.Empty).Value);
    }

    [Theory]
    [InlineData(null, ChannelKind.Switch, 8)]
    [InlineData("继电器", null, 8)]
    [InlineData("继电器", ChannelKind.Switch, null)]
    [InlineData("继电器", ChannelKind.Switch, 0)]
    [InlineData("继电器", ChannelKind.Switch, 257)]
    public void GenericGroup_MissingOrInvalidShape_IsRejected(string? name, ChannelKind? kind, int? count)
        => Assert.True(ModuleChannels.Resolve(Generic, new ModuleChannelConfig([new ChannelGroupSetting("g1", name, kind, count)])).IsFailure);

    [Fact]
    public void DuplicateOrMalformedKeys_AreStructuralErrors()
    {
        var errors = new List<Core.Domain.Common.Error>();
        new ModuleChannelConfig([new ChannelGroupSetting("relay"), new ChannelGroupSetting("relay"), new ChannelGroupSetting("Bad Key")])
            .CollectErrors(errors);

        Assert.Equal(2, errors.Count);
    }

    /// <summary>
    /// 按内容相等：模块从库里重新读出后，配置没变的绑定可以沿用（会话与轮询计数不重建）
    /// </summary>
    [Fact]
    public void Config_IsComparedByContent()
    {
        static ModuleChannelConfig Make() => new([new ChannelGroupSetting("relay", enabled: [3, 1], aliases: [new(1, "A")])]);

        Assert.Equal(Make(), Make());
        Assert.NotEqual(Make(), new ModuleChannelConfig([new ChannelGroupSetting("relay", enabled: [1], aliases: [new(1, "A")])]));
        Assert.NotEqual(ModuleChannelConfig.Empty, new ModuleChannelConfig([new ChannelGroupSetting("relay")]));
    }

    // ---------- 编辑表单 ----------

    [Fact]
    public void Form_AllEnabledWithoutAliases_SavesNothing()
    {
        var form = new ChannelFormViewModel();
        form.Load(RelayBoard, ModuleChannelConfig.Empty);

        Assert.True(form.IsVisible);
        Assert.False(form.IsManual);
        Assert.True(form.Collect().Value.IsEmpty);
    }

    [Fact]
    public void Form_DisablingAndNaming_RoundTrips()
    {
        var form = new ChannelFormViewModel();
        form.Load(RelayBoard, ModuleChannelConfig.Empty);
        var group = Assert.Single(form.Groups);
        group.DisableAllCommand.Execute(null);
        group.Channels[0].IsEnabled = true;
        group.Channels[0].Alias = "1# 电源继电器";

        var config = form.Collect().Value;
        var reloaded = new ChannelFormViewModel();
        reloaded.Load(RelayBoard, config);

        Assert.Equal([1], Assert.Single(config.Groups).Enabled);
        Assert.Equal([true, false], reloaded.Groups[0].Channels.Take(2).Select(c => c.IsEnabled));
        Assert.Equal("1# 电源继电器", reloaded.Groups[0].Channels[0].Alias);
        Assert.True(ModuleChannels.Resolve(RelayBoard, config).IsSuccess);
    }

    [Fact]
    public void Form_GenericModule_AddsGroups_AndRequiresName()
    {
        var form = new ChannelFormViewModel();
        form.Load(Generic, ModuleChannelConfig.Empty);
        Assert.True(form.IsManual);
        Assert.Empty(form.Groups);

        form.AddGroupCommand.Execute(null);
        form.AddGroupCommand.Execute(null);
        Assert.Equal(["g1", "g2"], form.Groups.Select(g => g.Key));
        Assert.True(form.Collect().IsFailure);

        form.Groups[0].Name = "继电器";
        form.Groups[0].CountInput = 4;
        form.Groups[1].Name = "在位";
        form.Groups[1].Kind = ChannelKindOption.All.Single(o => o.Kind == ChannelKind.Detect);
        form.Groups[1].RemoveCommand.Execute(null);

        var config = form.Collect().Value;
        var group = Assert.Single(ModuleChannels.Resolve(Generic, config).Value);
        Assert.Equal(("继电器", ChannelKind.Switch, 4), (group.Name, group.Kind, group.Count));
        Assert.Equal(4, form.Groups[0].Channels.Count);
    }
}
