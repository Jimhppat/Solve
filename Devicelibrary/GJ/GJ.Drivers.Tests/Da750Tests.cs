using System.Buffers.Binary;
using System.Text.Json;
using GJ.Drivers.Common;
using GJ.Drivers.Da750;
using Solve.Device.Sdk;

namespace GJ.Drivers.Tests;

public sealed class Da750Tests
{
    private const int Address = 1;

    /// <summary>
    /// 按旧驱动解析方式构造应答的模拟设备：数值直接给原始值，便于核对倍率
    /// </summary>
    private sealed class FakeDa750
    {
        public short[] VoltageRaw { get; } = new short[4];
        public short[] CurrentRaw { get; } = new short[4];
        public ushort[] Status { get; } = [0x0044, 0x0044, 0x0044, 0x0044];
        public byte Inverter { get; set; } = 0x44;

        /// <summary>
        /// 非 null 时所有请求都按此应答码应答（不带数据）
        /// </summary>
        public Ers272Code? ForcedCode { get; set; }

        public bool Silent { get; set; }

        public List<byte[]> Writes { get; } = [];

        public byte[]? Handle(byte[] request)
        {
            if (Silent)
                return null;

            Assert.True(Ers272Frame.TryParseRequest(request, out var address, out var command, out var info, out var checksumOk));
            Assert.True(checksumOk);
            Assert.Equal(Address, address);

            if (ForcedCode is { } code)
                return Ers272Frame.EncodeResponse(address, code, []);

            switch (command)
            {
                case Da750Protocol.ReadValues:
                    return Ers272Frame.EncodeResponse(address, Ers272Code.Ok, Values());
                case Da750Protocol.WriteSettings:
                    Writes.Add(info);
                    return Ers272Frame.EncodeResponse(address, Ers272Code.Ok, []);
                default:
                    return Ers272Frame.EncodeResponse(address, Ers272Code.InvalidCommand, []);
            }
        }

        private byte[] Values()
        {
            var info = new byte[Da750Protocol.ValuesInfoLength];
            for (var i = 0; i < 4; i++)
            {
                var slot = info.AsSpan(i * 8, 8);
                BinaryPrimitives.WriteInt16BigEndian(slot[..2], VoltageRaw[i]);
                BinaryPrimitives.WriteInt16BigEndian(slot[2..4], CurrentRaw[i]);
                BinaryPrimitives.WriteUInt16BigEndian(slot[6..8], Status[i]);
            }

            info[^1] = Inverter;
            return info;
        }
    }

    private static readonly Da750Driver Driver = new(Da750Model.Da750_4_60A);

    private readonly FakeDa750 _device = new();

    private static Da750Session NewSession(string von = "8", bool strict = true, bool undervoltageIsFault = true)
        => (Da750Session)Driver.CreateSession(Context(Address, von, strict, undervoltageIsFault));

    internal static DeviceContext Context(int address, string von = "8", bool strict = true, bool undervoltageIsFault = true)
        => new(address, LinkKind.Serial, new DeviceParameters(new Dictionary<string, string>
        {
            [Da750Driver.VonKey] = von,
            [Da750Driver.StrictFrameKey] = strict ? "true" : "false",
            [Da750Driver.UndervoltageFaultKey] = undervoltageIsFault ? "true" : "false",
        }));

    private ExchangeRunner.Run Poll(IDeviceSession session, int chunk = int.MaxValue)
        => ExchangeRunner.Execute(session.BeginPoll(), _device.Handle, chunk);

    private ExchangeRunner.Run Command(IDeviceSession session, string key, int channel, Dictionary<string, double>? arguments = null)
        => ExchangeRunner.Execute(session.BeginCommand(new DeviceCommand(key, arguments ?? new Dictionary<string, double>()) { Channel = channel }), _device.Handle);

    private static double Sample(ExchangeStep step, string key, int channel = 0)
        => Assert.IsType<ExchangeStep.Completed>(step).Samples.Single(s => s.Key == key && s.Channel == channel).Value;

    /// <summary>
    /// 写设定数据区中某一槽（0 起）的 6 个字节
    /// </summary>
    private static byte[] Slot(byte[] info, int slot) => info.AsSpan(slot * Da750Protocol.SettingSlotLength, Da750Protocol.SettingSlotLength).ToArray();

    // Von 8 V × 20 = 160 = 0x00A0
    private static readonly byte[] UnloadedSlot = [0x00, 0x00, 0xA0, 0x00, 0x00, 0x00];

    // ---------- 声明 ----------

    [Fact]
    public void Info_IsValid_AndDeclaresFourLoadChannels()
    {
        Assert.Empty(Driver.Info.Validate());
        Assert.Equal("gj.da750-4-60a", Driver.Info.Id);
        Assert.Equal(DeviceCategory.PowerAndLoad, Driver.Info.Category);
        Assert.Equal((1, 127), (Driver.Info.MinAddress, Driver.Info.MaxAddress));

        var group = Assert.Single(Driver.Info.Channels);
        Assert.Equal(("load", ChannelFunction.Load, 4), (group.Key, group.Function, group.Count));
    }

    [Fact]
    public void Info_FollowsElectronicLoadProfile()
    {
        // 对接规范中电子负载的标准 Key：测试流程只认这些
        var points = Driver.Info.Points.Where(p => p.Group == "load").Select(p => p.Key).ToHashSet();
        Assert.Superset(new HashSet<string> { "voltage", "current", "power", "input" }, points);

        var commands = Driver.Info.Commands.ToDictionary(c => c.Key);
        Assert.All(new[] { "load.cc", "load.cv", "load.off" }, key => Assert.Equal("load", commands[key].Group));
        Assert.All(commands.Values, c => Assert.True(c.Safety >= CommandSafety.Output, $"{c.Key} 会立即改变拉载，安全等级不能低于输出动作"));

        var current = Assert.Single(commands["load.cc"].Arguments, a => a.Key == "current");
        Assert.Equal(("A", 0.1, 60.0, true), (current.Unit, current.Minimum, current.Maximum, current.IsRequired));
        var voltage = Assert.Single(commands["load.cv"].Arguments);
        Assert.Equal(("voltage", "V", 5.0, 60.0, true), (voltage.Key, voltage.Unit, voltage.Minimum, voltage.Maximum, voltage.IsRequired));
        Assert.Empty(commands["load.off"].Arguments);
    }

    [Fact]
    public void Package_ListsDriver()
        => Assert.Equal(["gj.da750-4-60a"], new GJDriverPackage().Drivers.Select(d => d.Info.Id));

    [Fact]
    public void Package_DependsOnlyOnSdkAndFramework()
    {
        var references = typeof(GJDriverPackage).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, r => Assert.True(
            r == "Solve.Device.Sdk" || r.StartsWith("System", StringComparison.Ordinal) || r == "netstandard",
            $"驱动包引用了白名单外的程序集 {r}"));
    }

    [Fact]
    public void Manifest_MatchesCode()
    {
        var path = FindManifest();
        var text = File.ReadAllText(path);

        // 构建规则取第一个 "id" 作为输出目录名
        Assert.True(text.IndexOf("\"id\"", StringComparison.Ordinal) < text.IndexOf("\"drivers\"", StringComparison.Ordinal), "清单的 id 必须写在 drivers 之前");

        using var manifest = JsonDocument.Parse(text);
        var root = manifest.RootElement;
        Assert.Equal("gj.drivers", root.GetProperty("id").GetString());
        Assert.Equal(SdkInfo.MajorVersion, root.GetProperty("sdkVersion").GetInt32());
        Assert.Equal(typeof(GJDriverPackage).FullName, root.GetProperty("entryType").GetString());

        var declared = root.GetProperty("drivers").EnumerateArray()
            .Select(d => (d.GetProperty("id").GetString(), d.GetProperty("name").GetString(), d.GetProperty("category").GetString()))
            .ToList();
        var code = new GJDriverPackage().Drivers.Select(d => ((string?)d.Info.Id, (string?)d.Info.Name, (string?)d.Info.Category.ToString())).ToList();
        Assert.Equal(code, declared);
    }

    private static string FindManifest()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "GJ.Drivers", "package.json");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("找不到 GJ.Drivers/package.json");
    }

    // ---------- 轮询 ----------

    [Fact]
    public void Poll_SendsReadValuesFrame()
    {
        var run = Poll(NewSession());

        Assert.Equal([0xEE, 0x01, 0x0F, 0x03, 0x00, 0x13, 0xEF], Assert.Single(run.Sent));
    }

    [Fact]
    public void Poll_DecodesEveryChannel_WithLegacyScales()
    {
        // 通道 1：240 / 20 = 12 V，550 / 100 = 5.5 A，状态字 Bit 0 = 带载
        _device.VoltageRaw[0] = 240;
        _device.CurrentRaw[0] = 550;
        _device.Status[0] = 0x0045;
        _device.VoltageRaw[3] = 1199;

        var final = Poll(NewSession()).Final;

        Assert.Equal(12, Sample(final, "voltage", 1), 6);
        Assert.Equal(5.5, Sample(final, "current", 1), 6);
        Assert.Equal(66, Sample(final, "power", 1), 6);
        Assert.Equal(1, Sample(final, "input", 1));
        Assert.Equal(0x0045, Sample(final, "status-word", 1));
        Assert.Equal(0, Sample(final, "input", 2));
        Assert.Equal(59.95, Sample(final, "voltage", 4), 6);
        Assert.Equal(0x44, Sample(final, "inverter-status"));

        // 4 路 × 5 个通道点位 + 逆变器状态；不上报第 5–8 槽
        Assert.Equal(21, Assert.IsType<ExchangeStep.Completed>(final).Samples.Count);
    }

    [Fact]
    public void Poll_ArrivingByteByByte_Completes()
        => Assert.IsType<ExchangeStep.Completed>(Poll(NewSession(), chunk: 1).Final);

    [Fact]
    public void Poll_ChannelProtection_IsDeviceFault()
    {
        _device.Status[1] = 0x0044 | (1 << 5) | (1 << 8);

        var failed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final);

        Assert.Equal(FailureKind.Device, failed.Kind);
        Assert.Contains("通道 2", failed.Reason);
        Assert.Contains("过温保护", failed.Reason);
        Assert.Contains("OCP1", failed.Reason);
    }

    [Fact]
    public void Poll_InverterFault_IsDeviceFault()
    {
        _device.Inverter = 0x44 | (1 << 4);

        var failed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final);

        Assert.Equal(FailureKind.Device, failed.Kind);
        Assert.Contains("逆变器", failed.Reason);
        Assert.Contains("风扇故障", failed.Reason);
    }

    [Fact]
    public void Poll_Undervoltage_AlarmsOnlyWhenConfigured()
    {
        // 输入电压字段 Bit 2–3 = 00（欠压）
        _device.Status[2] = 0x0040;

        var alarmed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession(undervoltageIsFault: true)).Final);
        Assert.Equal(FailureKind.Device, alarmed.Kind);
        Assert.Contains("输入电压欠压", alarmed.Reason);

        Assert.IsType<ExchangeStep.Completed>(Poll(NewSession(undervoltageIsFault: false)).Final);
    }

    [Theory]
    [InlineData(0x0048, "输入电压过压")]
    [InlineData(0x004C, "输入电压状态无效")]
    [InlineData(0x0084, "直流母线过压")]
    public void Poll_OvervoltageOrUndefinedField_AlwaysAlarms(int status, string expected)
    {
        _device.Status[0] = (ushort)status;

        var failed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession(undervoltageIsFault: false)).Final);

        Assert.Equal(FailureKind.Device, failed.Kind);
        Assert.Contains(expected, failed.Reason);
    }

    [Theory]
    [InlineData(2000, 0)]
    [InlineData(-100, 0)]
    [InlineData(0, 7300)]
    public void Poll_ImplausibleReading_IsTransient_AndNotReported(short voltageRaw, short currentRaw)
    {
        _device.VoltageRaw[0] = voltageRaw;
        _device.CurrentRaw[0] = currentRaw;

        var failed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final);

        Assert.Equal(FailureKind.Transient, failed.Kind);
        Assert.Contains("超出合理范围", failed.Reason);
    }

    [Theory]
    [InlineData(0xF1, FailureKind.Transient)]
    [InlineData(0xF2, FailureKind.Transient)]
    [InlineData(0xF3, FailureKind.Configuration)]
    [InlineData(0xF4, FailureKind.Configuration)]
    [InlineData(0xF9, FailureKind.Transient)]
    public void Poll_DeviceErrorCode_IsClassified(byte code, FailureKind kind)
    {
        _device.ForcedCode = (Ers272Code)code;

        Assert.Equal(kind, Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final).Kind);
    }

    [Fact]
    public void Poll_OkWithoutData_IsTransient()
    {
        _device.ForcedCode = Ers272Code.Ok;

        var failed = Assert.IsType<ExchangeStep.Failed>(Poll(NewSession()).Final);

        Assert.Equal(FailureKind.Transient, failed.Kind);
        Assert.Contains("65 字节", failed.Reason);
    }

    // ---------- 命令 ----------

    [Fact]
    public void Cc_WritesEverySlot_OnlyTargetChannelLoaded()
    {
        var session = NewSession();

        var run = Command(session, "load.cc", 2, new() { ["current"] = 5.5, ["start-delay"] = 1 });

        Assert.IsType<ExchangeStep.Completed>(run.Final);
        var info = Assert.Single(_device.Writes);
        Assert.Equal(Da750Protocol.SettingsInfoLength, info.Length);

        // 通道 2：CC 慢速、Von 0x00A0、5.5 A × 100 = 0x0226、启动延迟 1 s × 10 = 0x0A；4 路重复一次填满 8 槽
        byte[] loaded = [0x00, 0x00, 0xA0, 0x02, 0x26, 0x0A];
        Assert.Equal([UnloadedSlot, loaded, UnloadedSlot, UnloadedSlot, UnloadedSlot, loaded, UnloadedSlot, UnloadedSlot],
            Enumerable.Range(0, 8).Select(i => Slot(info, i)));
        Assert.Equal(new Da750Setting(Da750Mode.CcSlow, 550, 10), session.Applied[1]);
    }

    [Fact]
    public void Cc_StartDelayDefaultsToZero()
    {
        Command(NewSession(), "load.cc-fast", 1, new() { ["current"] = 60 });

        // CC 快速 = 4，60 A × 100 = 6000 = 0x1770
        Assert.Equal([0x04, 0x00, 0xA0, 0x17, 0x70, 0x00], Slot(Assert.Single(_device.Writes), 0));
    }

    [Fact]
    public void Cv_EncodesVoltageTimesTwenty_ExtraZero()
    {
        Command(NewSession(von: "10"), "load.cv", 4, new() { ["voltage"] = 12.5 });

        // CV = 1，Von 10 V × 20 = 0x00C8，12.5 V × 20 = 250 = 0x00FA
        Assert.Equal([0x01, 0x00, 0xC8, 0x00, 0xFA, 0x00], Slot(Assert.Single(_device.Writes), 3));
    }

    [Fact]
    public void NextCommand_KeepsConfirmedSettingsOfOtherChannels()
    {
        var session = NewSession();
        Command(session, "load.cc", 2, new() { ["current"] = 5.5 });

        Command(session, "load.cv", 1, new() { ["voltage"] = 12 });

        var info = _device.Writes[1];
        Assert.Equal([0x01, 0x00, 0xA0, 0x00, 0xF0, 0x00], Slot(info, 0));
        Assert.Equal([0x00, 0x00, 0xA0, 0x02, 0x26, 0x00], Slot(info, 1));
    }

    [Fact]
    public void Off_UnloadsOnlyThatChannel()
    {
        var session = NewSession();
        Command(session, "load.cc", 1, new() { ["current"] = 3 });
        Command(session, "load.cc", 2, new() { ["current"] = 4 });

        Command(session, "load.off", 1);

        Assert.Equal(UnloadedSlot, Slot(_device.Writes[2], 0));
        Assert.Equal([0x00, 0x00, 0xA0, 0x01, 0x90, 0x00], Slot(_device.Writes[2], 1));
    }

    // ---------- 安全状态（SDK 1.3） ----------

    private ExchangeRunner.Run SafeState(IDeviceSession session, string? group = null, int channel = 0)
        => ExchangeRunner.Execute(Assert.IsAssignableFrom<ISafeStateProvider>(session).BeginSafeState(group, channel), _device.Handle);

    [Fact]
    public void SafeState_SingleChannel_UnloadsOnlyThatChannel_KeepsOthersFromMirror()
    {
        var session = NewSession();
        Command(session, "load.cc", 1, new() { ["current"] = 3 });
        Command(session, "load.cc", 2, new() { ["current"] = 4 });

        Assert.IsType<ExchangeStep.Completed>(SafeState(session, "load", 2).Final);

        var info = _device.Writes[^1];
        Assert.Equal([0x00, 0x00, 0xA0, 0x01, 0x2C, 0x00], Slot(info, 0));
        Assert.Equal(UnloadedSlot, Slot(info, 1));
        Assert.Equal(Da750Setting.Unloaded, session.Applied[1]);
    }

    [Fact]
    public void SafeState_SingleChannel_Failed_ChannelStaysUnloadedInMirror()
    {
        var session = NewSession();
        Command(session, "load.cc", 2, new() { ["current"] = 4 });
        _device.Silent = true;
        Assert.IsType<ExchangeStep.Failed>(SafeState(session, "load", 2).Final);
        _device.Silent = false;

        Command(session, "load.cc", 1, new() { ["current"] = 3 });

        Assert.Equal(UnloadedSlot, Slot(_device.Writes[^1], 1));
    }

    [Theory]
    [InlineData("load", 0)]
    [InlineData("load", 5)]
    [InlineData("output", 1)]
    [InlineData(null, 1)]
    public void SafeState_UndeclaredTarget_IsConfigurationError_WithoutSending(string? group, int channel)
    {
        var run = SafeState(NewSession(), group, channel);

        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeStep.Failed>(run.Final).Kind);
        Assert.Empty(run.Sent);
    }

    [Fact]
    public void SafeState_WritesEightUnloadedSlots_FrameVector()
    {
        var run = SafeState(NewSession());

        Assert.IsType<ExchangeStep.Completed>(run.Final);

        // EE ADR=01 CID=41 LEN=3+48=0x33，8 槽 × [模式 00, Von 00A0, 负载值 0000, 附加 00]，
        // CHK = 01 + 41 + 33 + 8 × A0 = 0x0575，EF
        byte[] expected =
        [
            0xEE, 0x01, 0x41, 0x33,
            .. Enumerable.Repeat(UnloadedSlot, Da750Protocol.SlotCount).SelectMany(s => s),
            0x05, 0x75, 0xEF,
        ];
        Assert.Equal(expected, Assert.Single(run.Sent));
    }

    [Fact]
    public void SafeState_UnloadsChannelsThatWereLoaded()
    {
        var session = NewSession();
        Command(session, "load.cc", 1, new() { ["current"] = 3 });
        Command(session, "load.cv", 2, new() { ["voltage"] = 12 });

        SafeState(session);

        var info = _device.Writes[2];
        Assert.All(Enumerable.Range(0, Da750Protocol.SlotCount), slot => Assert.Equal(UnloadedSlot, Slot(info, slot)));
        Assert.All(session.Applied, s => Assert.Equal(Da750Setting.Unloaded, s));
    }

    [Fact]
    public void SafeState_Failed_NextCommandStillDoesNotRestorePreviousLoad()
    {
        // 跳闸时设备没应答：设备状态未知，但复位后的下一条命令也不能把跳闸前的拉载写回去
        var session = NewSession();
        Command(session, "load.cc", 1, new() { ["current"] = 3 });
        _device.Silent = true;
        Assert.IsType<ExchangeStep.Failed>(SafeState(session).Final);
        _device.Silent = false;

        Command(session, "load.cc", 2, new() { ["current"] = 4 });

        var info = _device.Writes[^1];
        Assert.Equal(UnloadedSlot, Slot(info, 0));
        Assert.Equal([0x00, 0x00, 0xA0, 0x01, 0x90, 0x00], Slot(info, 1));
    }

    [Fact]
    public void SafeState_DeviceRejects_IsFailed()
    {
        _device.ForcedCode = Ers272Code.InvalidData;

        Assert.IsType<ExchangeStep.Failed>(SafeState(NewSession()).Final);
    }

    [Fact]
    public void RejectedWrite_DoesNotUpdateMirror()
    {
        var session = NewSession();
        _device.ForcedCode = Ers272Code.InvalidData;
        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeStep.Failed>(Command(session, "load.cc", 2, new() { ["current"] = 5 }).Final).Kind);
        _device.ForcedCode = null;

        Command(session, "load.cc", 3, new() { ["current"] = 1 });

        Assert.Equal(UnloadedSlot, Slot(Assert.Single(_device.Writes), 1));
    }

    [Fact]
    public void TimedOutWrite_DoesNotUpdateMirror()
    {
        var session = NewSession();
        _device.Silent = true;
        Assert.IsType<ExchangeStep.Failed>(Command(session, "load.cc", 2, new() { ["current"] = 5 }).Final);

        Assert.All(session.Applied, s => Assert.Equal(Da750Setting.Unloaded, s));
    }

    [Fact]
    public void SetpointWithEeLowByte_IsSentAsEd()
    {
        // 2.38 A × 100 = 238 = 0x00EE：协议把 EE 替代为 ED，实际下发 2.37 A（已知的协议有损，见 Ers272Frame）
        Command(NewSession(), "load.cc", 1, new() { ["current"] = 2.38 });

        Assert.Equal([0x00, 0x00, 0xA0, 0x00, 0xED, 0x00], Slot(Assert.Single(_device.Writes), 0));
    }

    public static TheoryData<string, int, Dictionary<string, double>> InvalidCommands => new()
    {
        { "load.cc", 1, new() },
        { "load.cc", 1, new() { ["current"] = 60.01 } },
        { "load.cc", 1, new() { ["current"] = 0.05 } },
        { "load.cc", 1, new() { ["current"] = double.NaN } },
        { "load.cc", 1, new() { ["current"] = 5, ["start-delay"] = 21 } },
        { "load.cc", 1, new() { ["current"] = 5, ["voltage"] = 12 } },
        { "load.cv", 1, new() { ["voltage"] = 4.9 } },
        { "load.cc", 0, new() { ["current"] = 5 } },
        { "load.cc", 5, new() { ["current"] = 5 } },
        { "load.cp", 1, new() { ["power"] = 100 } },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidCommand_IsConfiguration_AndSendsNothing(string key, int channel, Dictionary<string, double> arguments)
    {
        var run = Command(NewSession(), key, channel, arguments);

        Assert.Empty(run.Sent);
        Assert.Equal(FailureKind.Configuration, Assert.IsType<ExchangeStep.Failed>(run.Final).Kind);
    }
}
