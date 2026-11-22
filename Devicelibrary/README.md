# Solve 驱动开发规范（SDK 1.4）

本文写给为 Solve 开发设备驱动的工程师。按本文组织目录、编写清单与代码、完成测试，驱动包就能被驱动库扫描、经操作员信任后载入。

- 契约来源：`Solve.Device.Sdk`（驱动唯一可以引用的程序集）
- 参考实现：`Devicelibrary/Chroma/Chroma.Drivers`（程控电源）、`Devicelibrary/GJ/GJ.Drivers`（多通道电子负载，带参数命令）、`Sample.DriverPackage`（最小样板）
- 分类规范：电源 / 电子负载驱动的标准通道组、点位、命令与参数 Key 见 `Docs/电源与电子负载模块对接规范.md`；每个真实设备驱动在 `Docs/` 下附一份驱动对接说明（如 `Docs/GJ DA750-4-60A 驱动对接说明.md`）
- 规则的"为什么"写在 SDK 源码注释里；本文只写"怎么做"和"不能怎么做"。两者不一致时以 SDK 源码为准，并请修正本文。

---

## 1. 基本原则

| 原则 | 含义 |
|---|---|
| 驱动只编解码，不做 I/O | 打开串口 / Socket、收发、超时、重试、通道互斥、仿真切换、安全联锁全部由内核完成。驱动只负责"下一帧发什么"与"收到的字节是什么意思"。 |
| 只引用 SDK | 驱动项目只能引用 `Solve.Device.Sdk`，不得引用任何其他 `Solve.*` 程序集。 |
| 纯计算、立即返回 | `IExchange`、`ISimulatedDevice` 的方法必须在毫秒级内返回，不得阻塞、`Sleep`、等待、访问文件 / 网络 / 串口、启动线程或定时器。需要等待时用 `ExchangeStep.Send.DelayAfter` 交给内核。 |
| 可预期的失败不抛异常 | 校验错误、设备异常码、数值越界都返回 `ExchangeStep.Failed`。抛出的异常会被视为"通信状态未知"，内核对整条总线执行安全停止。 |
| 声明一经发布不得改名 | 驱动标识、点位 / 命令 / 参数 / 通道组的 Key、选项值：已挂载的模块和已有接线只按它们找回驱动。 |
| 必须带仿真设备 | 每个驱动都附带一台按同一协议应答的仿真设备（SDK 1.4 起载入时核对，缺少即载入失败）：不接设备也能在仿真模式下跑通轮询、命令与安全停止（见 5.7）。 |

---

## 2. 目录与命名

### 2.1 目录布局

驱动源码与编译产出的驱动包放在同一个驱动库根目录 `Devicelibrary/` 下：

```
Devicelibrary/
├─ Devicelibrary.slnx             ← 驱动库解决方案：全部驱动项目与测试（内核解决方案 Solve.slnx 不包含驱动）
├─ Directory.Build.props          ← 公共编译设置：源码没改时重新编译，驱动包逐字节相同（不要修改）
├─ Directory.Build.targets        ← 公共构建规则：驱动项目编译后自动输出驱动包（不要修改）
├─ README.md                      ← 本文
├─ Chroma/                        ← 厂家目录（源码）
│  ├─ Chroma.Drivers/             ← 驱动项目：*.csproj + package.json + 源码
│  │  ├─ Chroma.Drivers.csproj
│  │  ├─ package.json
│  │  ├─ ChromaDriverPackage.cs   ← 驱动包入口（IDriverPackage）
│  │  ├─ Common/                  ← 本厂家各型号共用的编解码
│  │  └─ Psu62000B/               ← 一个系列一个目录
│  └─ Chroma.Drivers.Tests/       ← 测试项目（没有 package.json）
└─ chroma.drivers/                ← 驱动包（编译自动生成，不进版本库，不要手工修改）
   ├─ Chroma.Drivers.dll
   ├─ package.json
   └─ .gitignore
```

扫描规则（`PackageScanner`）：

| 根目录下的一级子目录 | 扫描时当作 |
|---|---|
| 以 `.` 开头 | 跳过（导入时的临时目录） |
| 本层有 `*.csproj`，或本层没有 `package.json` 且下面两层内有 `*.csproj` | 源码目录：不当作驱动包；其中"同一目录有 `*.csproj` 与 `package.json`"的是驱动项目，驱动库里没有同一包标识的驱动包时显示为 **未编译** |
| 其他 | 驱动包：根部必须有 `package.json` 与入口程序集 |

### 2.2 命名

| 对象 | 规则 | 示例 |
|---|---|---|
| 厂家目录 | 厂家英文名，PascalCase；两个字母的缩写保持大写 | `Chroma`、`Itech`、`GJ` |
| 驱动项目 / 程序集 | `<厂家>.Drivers` | `Chroma.Drivers` |
| 测试项目 | `<厂家>.Drivers.Tests` | `Chroma.Drivers.Tests` |
| 命名空间 | 与项目同名，系列用子命名空间 | `Chroma.Drivers.Psu62000B` |
| 包标识 `id` | `<厂家小写>.drivers` | `chroma.drivers` |
| 驱动标识 | `<厂家小写>.<型号小写>`，型号中的空格换成 `-` | `chroma.62015b-15-90` |
| Key（点位 / 命令 / 参数 / 通道组） | 小写英文，单词间用 `-`，层级用 `.` | `voltage`、`output.on`、`command-delay-ms` |

标识与 Key 的格式：1–64 个字符，只含小写字母、数字、`.`、`-`、`_`（`KeyFormat.IsValid`）。

一个厂家一个驱动包，同一厂家的不同型号放在同一个包里作为多个驱动；不要一个型号一个包。

### 2.3 禁止事项

- **源码目录名不能与包标识相同**（Windows 不区分大小写）。驱动包输出目录 `Devicelibrary/<包标识>/` 每次编译都会整体删除重建；构建规则发现输出目录里有 `*.cs` / `*.csproj` 时会报错停止，不会删除，但请从一开始就避免。
- 不要把 SDK（`Solve.Device.Sdk.dll`）放进驱动包；不要手工修改 `Devicelibrary/<包标识>/` 下的文件。
- 不要在驱动包目录里放程序集与清单以外的东西（文档、日志、配置）。单包上限 512 个文件、64 MB。

---

## 3. 新建一个驱动项目

以厂家 Acme 为例。

**① 建目录与项目文件** `Devicelibrary/Acme/Acme.Drivers/Acme.Drivers.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <LangVersion>14</LangVersion>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <!-- SDK 由主程序提供：Private="false"，不复制进驱动包 -->
        <ProjectReference Include="..\..\..\Solve.Device.Sdk\Solve.Device.Sdk.csproj" Private="false" />
    </ItemGroup>

    <ItemGroup>
        <InternalsVisibleTo Include="Acme.Drivers.Tests" />
    </ItemGroup>

    <ItemGroup>
        <None Update="package.json" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>

</Project>
```

**② 写清单** `package.json`（见第 4 节）。

**③ 写入口类型、驱动与仿真设备**（见第 5 节；仿真设备与驱动同时写，见 5.7）。

**④ 建测试项目** `Devicelibrary/Acme/Acme.Drivers.Tests/`，照抄 `Chroma.Drivers.Tests.csproj`：引用驱动项目与 SDK（测试进程需要自己引用一份 SDK）。

**⑤ 加入驱动库解决方案** `Devicelibrary/Devicelibrary.slnx`，一个厂家一个文件夹：

```xml
<Folder Name="/Acme/">
  <Project Path="Acme/Acme.Drivers/Acme.Drivers.csproj" />
  <Project Path="Acme/Acme.Drivers.Tests/Acme.Drivers.Tests.csproj" />
</Folder>
```

不要加入内核解决方案 `Solve.slnx`；内核的任何项目（包括测试项目）也不要引用驱动项目，只管构建顺序的引用也不行。这样编译内核时不会编译驱动，驱动包只在改了驱动之后才重新生成，驱动库不会在源码没变时要求重新信任。

**⑥ 编译**：`dotnet build "Devicelibrary/Devicelibrary.slnx"`（或在 IDE 里打开这个解决方案编译），成功时输出 `驱动包 acme.drivers 已输出到 …\Devicelibrary\acme.drivers`。

---

## 4. 清单 package.json

清单是操作员信任时看到的内容：扫描只读清单、不执行代码就能列出包内驱动；载入时内核逐项核对代码与清单，不一致就拒绝载入。

```json
{
  "id": "acme.drivers",
  "name": "Acme 驱动包",
  "version": "1.0.0",
  "author": "Acme",
  "sdkVersion": 1,
  "entryAssembly": "Acme.Drivers.dll",
  "entryType": "Acme.Drivers.AcmeDriverPackage",
  "drivers": [
    { "id": "acme.psu-3000", "name": "Acme PSU-3000", "category": "PowerAndLoad", "manufacturer": "Acme", "model": "PSU-3000" }
  ]
}
```

| 字段 | 必填 | 规则 |
|---|---|---|
| `id` | 是 | 包标识，标识格式，最长 64。**必须写在 `drivers` 之前**：构建规则取文件中第一个 `"id"` 作为输出目录名。 |
| `name` | 是 | 驱动库窗口中显示的包名，最长 64 |
| `version` | 是 | 语义化版本 `主.次.修订`，最长 64。改了代码就要改版本（见第 7 节） |
| `author` | 是 | 作者或厂家，最长 64 |
| `sdkVersion` | 是 | 整数，必须等于 `SdkInfo.MajorVersion`（当前为 `1`），否则显示"版本不兼容" |
| `entryAssembly` | 是 | 入口程序集文件名，必须是包目录根部的 `.dll`，不能含路径 |
| `entryType` | 是 | 实现 `IDriverPackage` 的公开类型全名，最长 256 |
| `drivers` | 是 | 1–64 项，包内全部驱动，必须与代码中 `IDriverPackage.Drivers` 一一对应 |
| `drivers[].id` | 是 | 驱动标识，与代码 `DriverInfo.Id` 完全一致，包内不重复，且不能与其他包或内置驱动重复 |
| `drivers[].name` | 是 | 与代码 `DriverInfo.Name` 完全一致（区分大小写、空格） |
| `drivers[].category` | 是 | `DeviceCategory` 成员名：`General`、`PowerAndLoad`；与代码一致；不接受数字 |
| `drivers[].manufacturer` / `model` | 否 | 厂家、型号，各最长 64 |

清单允许注释与尾随逗号，但请保持标准 JSON。

---

## 5. 代码契约

### 5.1 驱动包入口 `IDriverPackage`

```csharp
public sealed class AcmeDriverPackage : IDriverPackage
{
    public IReadOnlyList<IDeviceDriver> Drivers { get; } = [new AcmePsuDriver(AcmePsuModel.Psu3000)];
}
```

- 公开、非抽象、有公开无参构造函数。
- 构造函数与 `Drivers` 只做声明：程序启动时就会被调用，不得访问文件、网络、串口、注册表，不得启动线程。

### 5.2 驱动 `IDeviceDriver` 与驱动说明 `DriverInfo`

驱动类型用 `internal sealed`，在构造函数里一次性建好 `Info`：

| 成员 | 规则 |
|---|---|
| `Id` / `Name` / `Category` | 与清单一致 |
| `Description` | 一句话：系列、额定参数、协议 |
| `MinAddress` / `MaxAddress` | 设备在总线上的合法地址，1–255。协议里没有地址（如纯文本透传）时限定为 `1..1`，防止两个模块的命令发到同一台设备 |
| `Points` | 点位：`Key`、显示名、物理单位（无单位填空串）、读写权限 |
| `Commands` | 命令：`Key`、显示名、安全等级（见 5.5）；需要设定值的命令用 `Arguments` 声明参数（SDK 1.2，见 5.5） |
| `Parameters` | 模块参数：同一型号在不同现场的不同配置（报文封装、延时、量程）。必须有合法默认值 |
| `Channels` | 通道组：可以单独接线的端子。单路设备也声明一组、数量 1；不声明就不能接到设备拓扑上 |

点位 / 命令属于某一路通道时设置 `Group = 通道组 Key`，上报 / 执行时用通道号（1..Count）区分；模块级的保持 `null`。

**通道数量与编号**：模块有几路通道，由驱动在 `Channels` 里按组声明（`ChannelGroupInfo.Count`），主程序不另外配置、也不猜测；模块的通道总数 = 各组数量之和。例如 4 路电子负载声明一组 `new ChannelGroupInfo("load", "负载", ChannelFunction.Load, 4)`。

主程序把每一路通道统一编号为 **CH + 通道号**：

| 项 | 规则 |
|---|---|
| 编号 | `CH1`..`CHn`，n = 该组的 `Count`；通道号就是 `DeviceCommand.Channel`、`PointSample.Channel`、`BeginSafeState(group, channel)` 的 `channel`，从 1 开始，不另起编号 |
| 与设备对应 | 通道号 1 必须是设备面板 / 端子排上的第 1 路（丝印 CH1）：现场照着硬件映射页的 CH 编号接线、查线。设备手册从 0 编号时，驱动在编解码时换算，对外一律从 1 开始 |
| 显示名 | 组名 + 编号，如"负载 CH3"；单路组同样带编号，如"输出 CH1"。模块配置里给某一路起了别名时显示别名 |
| 多组 | 同一模块的不同组各自从 CH1 编号（如"输出 CH1"与"继电器 CH1"），界面用组名区分，所以组名要短而明确 |

模块实例可以在模块配置里只启用其中几路（停用的端子不出现在硬件映射页），但不能超出驱动声明的数量。

Key 常量集中定义在驱动类型里（`public const string VoltageKey = "voltage";`），会话、仿真器、测试都引用常量，不要散落字符串字面量。

`CreateSession(DeviceContext context)`：每个模块调用一次，读取 `context.Parameters` 后构造会话。`context.Address` 已按地址范围校验，`context.Parameters` 已校验并补全默认值。

### 5.3 会话 `IDeviceSession`

- 一个模块一个会话，可以保存该模块的协议状态（序号、分段读取进度、"上次是否成功"）。
- 内核保证同一会话的调用串行进行，会话内部**不要加锁**。
- `BeginPoll()` 读取全部点位；`BeginCommand(command)` 执行一条命令；会改变输出的设备另实现 `ISafeStateProvider.BeginSafeState(group, channel)`（见 5.8）。
- 不支持的命令或参数无效：返回第一步就是 `Failed(…, FailureKind.Configuration)` 的交互，**不发送任何字节**（参考 `Chroma.Drivers/Common/FailedExchange.cs`）。

### 5.4 报文交互 `IExchange`

一次轮询或一条命令是一个由内核推进的小状态机：

```
step = Start()
while step 是 Send:
    内核发送 Request → 在超时内接收，每收到一块就调用 Match(累计字节)
    Match 返回 Complete → step = OnFrame(完整一帧)
    Match 返回 Invalid 或超时 → 交互失败（Transient）
step 是 Completed（上报点位）或 Failed（原因 + 分类）
```

实现要求：

1. **用显式的阶段枚举**推进多步交互（参考 `PollExchange.Stage`），不要用多个 bool 拼状态。
2. **`Match` 负责成帧**：校验帧头、地址、长度、CRC / 校验和；不够一帧返回 `NeedMoreData`；不可能成为有效响应时立即返回 `Invalid`（不要等到超时）；设一个最大帧长，超过仍未成帧即 `Invalid`。
3. **粘包**：`Complete` 时 `frameLength` 只覆盖这一帧，之后多余的字节由内核丢弃并记入追溯日志。
4. **只支持一问一答**（SDK 1）：每次发送前内核清空接收缓冲，设备主动上报的数据会被丢弃。
5. **时间参数**放在 `Send` 上，不要在驱动里等待：

| 参数 | 用途 | 上限（`ExchangeLimits`） |
|---|---|---|
| `ResponseTimeout` | 本帧响应超时，`null` 用总线配置 | 60 s |
| `GapBefore` | 发送前通道空闲时间，如 `FrameGap.ModbusRtu` | 100 字符 / 1 s |
| `DelayAfter` | 本帧结束后的等待，如命令执行后的稳定时间 | 10 s |

超出上限的步骤按 `Configuration` 失败。

6. **失败分类** `FailureKind` 决定内核如何重试与报警，必须选对：

| 分类 | 什么时候用 | 内核行为 |
|---|---|---|
| `Transient` | 干扰、校验错误、响应格式错乱、数值明显不合理、设备忙 | 按策略重试，连续失败后模块转为通信异常 |
| `Configuration` | 型号不符、地址或参数与设备不符、设备不支持该功能 | 不自动重试，标记为配置错误，直到配置被修改 |
| `Device` | 设备自己报告故障 / 保护（报警字、故障码、自检失败） | 产生报警，按较长间隔继续轮询观察恢复 |

7. `Failed.Reason` 面向操作员，写清楚发生了什么，如"设备返回异常码 02（非法数据地址）"、"电源报警：过压保护"。

### 5.5 命令与安全等级

| `CommandSafety` | 用于 | 内核检查（`DeviceCommandPlan` / `DeviceCommandService`） |
|---|---|---|
| `Parameter` | 写设定值，不直接改变输出 | 与轮询串行执行 |
| `Output` | 开关输出、启停；**写入即生效的设定**（如电子负载的带载设定） | 下游拓扑有停用位置时拒绝（联锁）；实机模式下需要操作员确认 |
| `Hazardous` | 高压、大电流、不可逆操作 | 联锁；仿真与实机模式下每次都需要操作员确认 |

安全等级宁高勿低。驱动不能绕过内核的检查，也不要在驱动里自己实现"确认"。

对输出类命令，执行后应**回读确认**设备状态（参考 `OutputExchange`：发送开关命令 → `DelayAfter` → 查询输出状态，与期望不符则失败），不要只发不验。

**命令参数（SDK 1.2）**：需要数值的命令（设定电流、电压、时长）在 `CommandInfo.Arguments` 中用 `CommandArgumentInfo` 声明每个参数：

```csharp
new CommandInfo("load.cc", "恒流带载", CommandSafety.Output)
{
    Group = "load",
    Arguments =
    [
        // 必填：没有默认值
        new CommandArgumentInfo("current", "电流", "A", Minimum: 0.1, Maximum: 60),
        // 可选：调用方不传时内核补默认值
        new CommandArgumentInfo("start-delay", "启动延迟", "s", 0, 20) { DefaultValue = 0 },
    ],
}
```

- 内核据此拒绝缺少必填参数、未声明的参数、越界与非有限值，补全默认值后再交给 `BeginCommand`；参数值写进确认文案与命令日志。
- 驱动编码前**按同一份声明再校验一次**（`CommandArgumentInfo.Check`），不合法即 `Failed(…, Configuration)` 且不发送任何字节（参考 `Da750Session.TryArgument`）。
- 参数值是工程值，单位即声明的 `Unit`；范围取设备额定值，没有依据时取保守范围。
- 一经发布，参数 Key 与单位不得修改，范围只能放宽；新增参数必须带默认值。
- 没有声明参数的命令保持 SDK 1.1 行为：调用方传入的参数原样转交，由驱动自行校验。
- 用到 `Arguments` 的驱动包要求主程序的 SDK 不低于 1.2（`sdkVersion` 仍为 1）。

### 5.6 数值、单位与字节序

- `PointSample.Value` 是**工程值**：已按协议乘倍率、加偏移、换算到 `PointInfo.Unit`。
- 在解析代码旁用注释写明：字节序 / 字序（大端、小端、CDAB 等）、有符号 / 无符号、倍率与偏移、原始单位。没有手册依据的数值不要猜，做成模块参数并给出保守默认值。
- 对设备返回的每个数值做合理性检查（按额定值给出合理范围，超出视为错乱、返回 `Transient`，参考 `Chroma62000BProtocol.TryReading`），不要把错乱的数据上报给内核。
- 解析数字一律用 `CultureInfo.InvariantCulture`，拒绝 `NaN` 与无穷。
- 浮点数不做 `==` 比较，用容差或整数原始值比较。

### 5.7 仿真设备 `IDeviceSimulatorProvider`（SDK 1.4 起必须实现）

每个驱动类型都必须同时实现 `IDeviceSimulatorProvider`。设备库载入驱动包时逐个核对：包内任何一个驱动没有实现，**整个驱动包载入失败**，驱动库显示原因，生产与仿真两种模式下都不能使用。这样挂到总线上的每个模块在仿真模式下都有设备应答，而不是按超时报通信异常。

```csharp
internal sealed class AcmePsuDriver : IDeviceDriver, IDeviceSimulatorProvider
{
    public ISimulatedDevice CreateSimulator(DeviceContext context)
        => new AcmePsuSimulator(_model, context.Address);
}
```

仿真模式下的整条链路（内核负责，驱动只提供会话与仿真设备）：

```
轮询 / 命令 / 安全停止 → 驱动会话编码请求 → 总线通道锁 → 仿真通道（不打开任何串口 / Socket）
    → 仿真设备分派：把请求交给这条总线上每个模块的仿真设备
    → ISimulatedDevice.Respond：按协议认领并返回应答帧
    → 驱动会话 Match / OnFrame 解码 → 模块读数、接线就绪、命令结果
```

实现要求：

| 项 | 要求 |
|---|---|
| 构造 | `CreateSimulator(context)` 只做构造，规则同 `IDriverPackage`；`context` 与同一模块的 `CreateSession` 收到的相同 |
| 认领 | 按同一协议判断请求是不是发给自己的（核对地址、帧头、校验）；不是、无法识别、按协议本就不应答（设置命令、广播）时返回 `null`。协议里没有地址的设备（如 SCPI 字节流）每条总线只挂一台，应答全部请求 |
| 应答 | 返回完整一帧，与实机字节级一致（帧头、长度、校验、结束符）：会话代码不为仿真写任何特殊分支 |
| 覆盖 | 驱动会发出的每一种请求都能应答：轮询、每条命令（含回读确认）、`ISafeStateProvider` 的安全状态报文 |
| 状态 | 记住命令造成的状态（输出开关、带载设定），读数随之变化：打开输出 / 带载后读数非零，关闭 / 卸载 / 安全状态后归零；上电默认输出关闭、全部卸载 |
| 异常码 | 对校验错、长度错、不认识的命令回应与实机相同的异常码，便于在仿真里跑通失败分类 |
| 读数 | 固定、可预期的工程值（如额定值的固定比例、固定的源电压），不用随机数；数值写在仿真类注释里，并列进该设备的驱动对接说明 |
| 身份 | 有身份查询的协议，仿真身份要能和实机区分（如 `*IDN?` 版本字段写 `SIM`）；没有身份字段的协议由内核命令日志的"模式"字段区分 |
| 约束 | 纯计算、立即返回；应答不超过 64 KB（`ISimulatedDevice.MaxResponseLength`）；内核保证串行调用，不需要加锁；抛出的异常只让这一次请求没有应答 |

参考实现：`GJ.Drivers/Da750/Da750Simulator.cs`（多台同总线、按地址认领、整帧校验后才生效）、`Chroma.Drivers/Psu62000B/Chroma62000BSimulator.cs`（无地址的 SCPI 字节流）。

内核的行为：

- 仿真设备按模块绑定缓存：同一模块在本次运行中沿用同一台仿真设备，输出状态在断开、急停、复位、重新连接之后仍然保持；模块或所在总线的配置变化、驱动重新载入后重新创建，状态从头开始。
- 同一条总线上两台仿真设备同时应答同一请求视为总线冲突，本次不应答并告警（地址重复或认领规则写错）。
- 实机（生产）模式下内核绝不创建、也绝不调用仿真设备。生产 / 仿真由主窗口状态栏的胶囊开关切换，保存后程序重启生效；切到生产要求本机以 `--hardware` 启动。

### 5.8 安全状态 `ISafeStateProvider`（SDK 1.3，会改变输出的设备必须实现）

会话类型同时实现 `ISafeStateProvider`，内核在两种场合把设备带到安全状态，走同一个驱动入口：

```csharp
internal sealed class Da750Session : IDeviceSession, ISafeStateProvider
{
    // group = null, channel = 0：整台模块
    // group = 通道组 Key, channel = 1..数量：这一路（编号同 DeviceCommand.Channel）
    public IExchange BeginSafeState(string? group, int channel) => /* 卸载目标通道 */;
}
```

| 场合 | 目标 | 内核行为 |
|---|---|---|
| 总线安全跳闸（操作员急停、驱动抛异常、监督循环异常、进程级异常） | 整台模块 `(null, 0)` | 锁存 SafeStop（从这一刻起其他报文一律被拒）→ 等在途事务让出通道 → 按地址顺序对该总线上每个实现了本接口的模块执行 → 关闭物理通道。只在跳闸前总线"已连接"时执行；同一条总线共用总时限 `BusRuntimeOptions.SafeStopTimeout`（默认 3 s）；一个模块失败、超时或抛异常只记严重日志，继续下一个，都不阻止跳闸 |
| 操作员按位置安全停止（`DeviceCommandService.SafeStopAsync`） | 接线的那一路，或整台模块 | 安全方向的专用操作：免除停用位置联锁与操作员确认；接线唯一、模块就绪、通道已声明、总线已连接照常检查；每次下发与结果都写结构化日志 |

普通的关闭 / 卸载命令（如 `output.off`、`load.off`）仍按其安全等级受联锁约束，驱动不要为了绕过联锁把它们做成别的样子。

实现要求：

- **只往安全方向动**：负载卸载、电源关闭输出、继电器断开；只动目标通道（整机时为全部通道）；绝不打开任何输出。
- **不依赖可能已失效的会话状态**：跳闸时上一次交互可能刚超时或异常，设备实际状态未知。不要先确认型号再关闭（确认失败会让关闭也发不出去，参考 `Chroma62000BSession.BeginSafeState`）；一帧写全部通道的协议，整机安全状态直接写"全部卸载"，不按镜像拼装（单路时其他各路才按镜像重发）。
- **镜像跟着复位**：维护设定镜像的会话，在 `BeginSafeState` 时就把镜像中的目标通道改成卸载，不等应答——下发失败时设备状态未知，之后的命令也绝不能把安全停止前的设定写回设备（参考 `Da750Session.BeginSafeState`）。
- **整机必须支持**；单路做不到的设备、未声明的通道组或越界的通道号，第一步就返回 `Failed(…, Configuration)`，不发送任何字节。
- **步数少、超时短**：一问一答或"设置 + 回读"即可；有回读的协议照样回读，与期望不符返回 `Device`，让日志如实记下"没有关掉"。
- 其余同 `IExchange` 的约定：纯计算、不阻塞、可预期的失败返回 `Failed`。内核同样保证与该会话的其他调用串行。
- 安全状态是软件层面的尽力而为：进程被强制结束、上位机断电、通信线断开时发不出去，不能替代设备自身的保护与通信看门狗。
- 实现了本接口的驱动包要求主程序的 SDK 不低于 1.3（`sdkVersion` 仍为 1）。

---

## 6. 测试规范

每个驱动包必须有测试项目，只依赖驱动项目与 SDK，不依赖内核。参考 `Chroma.Drivers.Tests`，至少覆盖：

| 类别 | 必测内容 |
|---|---|
| 声明 | `Info.Validate()` 返回空列表；驱动包入口列出全部驱动；程序集只引用 SDK 与 .NET 基础库（参考 `Package_DependsOnlyOnSdkAndFramework`） |
| 清单一致 | `package.json` 中每个驱动的 `id` / `name` / `category` 与代码一致（读取项目目录下的清单比对） |
| 正常流程 | 轮询读出全部点位且数值换算正确；每条命令的报文与回读确认 |
| 成帧 | 分包到达（逐字节喂给 `Match`）、粘包（帧后多余字节）、校验错误、非法字节、超长无结束符 |
| 失败分类 | 超时 → `Transient`；型号不符 → `Configuration`；设备报警 → `Device`；不支持的命令不发送任何字节 |
| 命令参数 | 每条带参数命令的报文编码（倍率、舍入）；缺参数、未声明参数、越界、`NaN`、无效通道号都按 `Configuration` 失败且不发送任何字节；可选参数的默认值 |
| 数值边界 | 越界、格式错乱、`NaN` 都不上报 |
| 仿真设备 | 包内每个驱动都实现了 `IDeviceSimulatorProvider`（参考 `Package_EveryDriverProvidesSimulator`）；用仿真设备跑一遍轮询、每条命令与安全状态，读数随命令变化、卸载 / 关闭后归零；不是发给自己的请求返回 `null`；错误请求回应与实机相同的异常码 |
| 安全状态 | 整机与单路 `BeginSafeState` 的完整报文向量；新会话（型号未确认、镜像为初始值）也能直接发出；带载 / 输出后执行确实卸载 / 关闭目标、不动其他通道；未声明的目标按 `Configuration` 失败且不发送任何字节；设备不应答或拒绝时返回 `Failed`，且之后的命令不会恢复跳闸前的设定 |

用一个测试辅助类代替内核推进交互（参考 `ExchangeRunner`）：发送 → 模拟设备应答 → 按块调用 `Match` → `OnFrame`。

本仓库内的驱动包另有内核侧的整链路回归 `Solve.Core.Tests/Composition/DriverPackageSimulationTests`：用 `Devicelibrary/<包标识>/` 下编译好的驱动包，在仿真模式的组合根里走一遍"信任 → 挂模块 → 接线 → 连接总线 → 下发命令 → 安全停止"，核对读数随命令变化、安全停止后归零。新增驱动包时在这里加一个用例。这个测试与主程序一样只读取已经编译好的驱动包，不会触发驱动编译：先编译驱动库，再运行内核测试；找不到驱动包时测试失败并提示，不会跳过。

运行单个驱动包的测试 / 驱动库的全部测试：

```powershell
dotnet test "Devicelibrary/Acme/Acme.Drivers.Tests/Acme.Drivers.Tests.csproj"
dotnet test "Devicelibrary/Devicelibrary.slnx"
```

---

## 7. 版本与兼容

**一经发布不得修改**：驱动标识；点位、命令、参数、通道组的 Key；参数选项值；通道号的含义。

| 变更 | 是否允许 | 要求 |
|---|---|---|
| 新增点位、命令 | 允许 | 次版本 +1 |
| 新增参数 | 允许 | 必须有默认值，已有模块按默认值运行；次版本 +1 |
| 新增通道组、增大通道数量 | 允许 | 次版本 +1 |
| 修复协议缺陷，声明不变 | 允许 | 修订号 +1 |
| 删除 / 改名上述任何 Key、减少通道数量、改变通道号含义 | **禁止** | 已有模块与接线会失效；确需替换时新建一个驱动标识 |
| 新增型号 | 允许 | 在同一个包里新增驱动，清单与代码同时加 |

SDK 契约出现不兼容修改时，SDK 主版本与 `SdkInfo.MajorVersion` 一起加一，所有驱动包需要按新 SDK 重新编译并把 `sdkVersion` 改为新值。

SDK 1.4 把仿真设备由"强烈建议"改为"必须"：契约类型没有变化，`sdkVersion` 仍为 `1`，已实现仿真设备的驱动包不需要重新编译；没有实现的驱动包在 1.4 起的主程序上载入失败，补上仿真设备、次版本 +1 后重新交付。

---

## 8. 编译、扫描与信任

### 8.1 开发调试流程

1. **编译驱动库**：`dotnet build "Devicelibrary/Devicelibrary.slnx"`（或在 IDE 里打开这个解决方案编译）。驱动项目编译完成后，`Directory.Build.targets` 自动把入口程序集、包内依赖与清单复制到 `Devicelibrary/<包标识>/`。
   > **编译内核不会编译驱动**：内核解决方案 `Solve.slnx` 不包含驱动项目，内核的任何项目也不引用驱动。改了驱动代码后，先编译驱动库，再运行主程序。

   > **驱动只在本仓库目录里编译，不要加 `--artifacts-path`**：dll 里记录着 obj 目录下 pdb 的绝对路径，换输出目录编出的驱动包字节不同，需要重新信任。
2. 打开主程序 → 模块配置 → **驱动库** → 点 **扫描**。
3. 选中驱动包，确认名称、版本、作者、指纹后点 **信任并载入**。驱动立即出现在模块配置的驱动表中，不需要重启。
4. 源码没改时重新编译，驱动包逐字节相同，指纹不变（见 `Directory.Build.props`）。改了驱动源码、SDK 增删了类型 / 成员，或切换了 Debug / Release 后重新编译，指纹才会变，驱动包回到"内容有变化"，需要重新信任（信任按指纹记忆，这是有意的）。
5. 已载入的旧版本代码无法在运行中卸载：信任新版本后显示"重启后生效"，重启主程序才会换成新版本。

### 8.2 扫描结果的状态

| 状态 | 含义 | 处理 |
|---|---|---|
| 未编译 | 驱动库里有这个驱动项目的源码，但没有它编译出的驱动包 | 编译驱动库解决方案后重新扫描 |
| 新发现 | 首次出现的驱动包，未执行任何代码 | 核对后信任并载入，或忽略此版本 |
| 内容有变化 | 曾经信任过，文件指纹已变（改了源码或 SDK 后重新编译、被替换） | 核对后重新信任 |
| 已载入 | 已信任并载入，驱动在驱动表中 | — |
| 重启后生效 | 已信任新版本，本次运行仍使用已载入的旧版本 | 重启主程序 |
| 已撤销信任 / 已忽略 | 操作员的选择 | 可随时重新信任 |
| 无效 | 清单缺失或字段错误、找不到入口程序集、源码项目的清单无效 | 按提示修改清单后重新编译 |
| 版本不兼容 | `sdkVersion` 或编译时引用的 SDK 主版本与主程序不一致，或分类本程序不支持 | 用当前 SDK 重新编译，或更新主程序 |
| 冲突 | 包标识或驱动标识与其他包 / 内置驱动重复 | 改标识（未发布前）或移走重复的包 |
| 载入失败 | 入口类型无效、驱动说明无效、代码与清单不一致、驱动没有实现仿真设备（SDK 1.4）、构造时抛异常 | 按提示修复；先跑测试 |

扫描"共 0 个驱动包"时依次检查：设置页中驱动库目录是否指向本目录；驱动项目是否已编译（看 `Devicelibrary/<包标识>/` 是否存在）；清单的 `id` 是否写在 `drivers` 之前。

### 8.3 交付到现场

- 现场只需要驱动包目录：把 `Devicelibrary/<包标识>/` 整个目录复制到现场驱动库目录下，或在驱动库窗口用 **从目录导入** 选择该目录（或项目的 `bin/<配置>/net10.0` 输出目录）。
- 不要把源码、测试项目、`bin` / `obj` 复制到现场驱动库。
- 交付时同时提供版本号与完整指纹（驱动库窗口中悬停指纹可看到完整值），现场操作员信任前核对指纹。

---

## 9. 提交前检查清单

- [ ] 项目只引用 `Solve.Device.Sdk`，且 `Private="false"`
- [ ] 清单 `id` 写在 `drivers` 之前；驱动的 `id` / `name` / `category` 与代码一致
- [ ] 源码目录名与包标识不同
- [ ] 驱动包入口与驱动构造函数不访问任何外部资源
- [ ] 交互用阶段枚举推进；`Match` 校验完整（长度、地址、校验、最大帧长）
- [ ] 可预期的失败返回 `Failed` 且分类正确，没有抛异常的路径
- [ ] 不支持的命令不发送任何字节
- [ ] 输出类命令有回读确认；安全等级宁高勿低
- [ ] 会改变输出的设备（电源、电子负载、继电器）实现了 `ISafeStateProvider`：整机与单路都能卸载 / 关闭，不先确认型号，镜像同步复位
- [ ] 需要设定值的命令用 `Arguments` 声明参数，驱动按同一份声明再校验
- [ ] 电源 / 电子负载驱动遵循 `Docs/电源与电子负载模块对接规范.md` 的标准 Key，并在 `Docs/` 下附驱动对接说明
- [ ] 数值注释写明字节序、符号、倍率、偏移、单位；有合理性检查；数字解析用不变区域性
- [ ] 每个驱动都实现了 `IDeviceSimulatorProvider`（SDK 1.4 起必须）：轮询、全部命令与安全状态都能应答，读数随命令变化，仿真身份可与实机区分
- [ ] 在仿真模式下实际跑过一遍：信任 → 挂模块 → 连接总线 → 模块上线 → 下发命令读数变化 → 急停后输出关闭
- [ ] 测试覆盖第 6 节全部类别，`dotnet test` 通过
- [ ] 已加入 `Devicelibrary/Devicelibrary.slnx`（不加入 `Solve.slnx`，内核项目不引用驱动项目）；改了代码已按第 7 节更新版本号
- [ ] 编译后驱动库扫描显示"新发现"或"内容有变化"，信任后状态为"已载入"
