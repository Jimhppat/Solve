using Microsoft.Extensions.Logging.Abstractions;
using Solve.Adapters.DeviceLibrary;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Runtime;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Core.Domain.Drivers;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.Devices;

public sealed class DeviceServiceTests
{
    /// <summary>
    /// 只记录"哪条总线被要求重新绑定"
    /// </summary>
    private sealed class RecordingBindings : IDeviceBindings
    {
        public List<BusId> Refreshed { get; } = [];

        public IReadOnlyList<DriverBinding> Get(BusId busId) => [];

        public Task RefreshAsync(BusId busId, CancellationToken cancellationToken)
        {
            Refreshed.Add(busId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 内存仓储：记录写入，按地址判断冲突，验证服务的预检与无效输入不触达存储
    /// </summary>
    private sealed class MemoryRepository : IDeviceRepository
    {
        public List<DeviceDescriptor> Devices { get; } = [];

        public int Writes { get; private set; }

        public Task<Result<IReadOnlyList<DeviceDescriptor>>> ListByBusAsync(BusId busId, CancellationToken cancellationToken)
            => Task.FromResult(Result<IReadOnlyList<DeviceDescriptor>>.Success(Devices.Where(d => d.BusId == busId).ToList()));

        public Task<Result<int>> CountByBusAsync(BusId busId, CancellationToken cancellationToken)
            => Task.FromResult(Result<int>.Success(Devices.Count(d => d.BusId == busId)));

        public Task<Result> AddRangeAsync(IReadOnlyList<DeviceDescriptor> devices, CancellationToken cancellationToken)
        {
            Writes++;
            Devices.AddRange(devices);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<int>> DeleteRangeAsync(IReadOnlyList<DeviceId> ids, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult(Result<int>.Success(Devices.RemoveAll(d => ids.Contains(d.Id))));
        }
    }

    private static readonly DriverId Generic = DriverCatalog.GenericModule;
    private static readonly DriverId Power = DriverId.Of("test.power");

    // 测试目录：通用模块 1–255，电源 1–10（验证驱动自身的地址范围）
    private static readonly DriverCatalog Catalog = new(
    [
        new DriverDescriptor(Generic, "通用模块", HardwareCategory.General, "", 1, 255),
        new DriverDescriptor(Power, "程控电源", HardwareCategory.PowerAndLoad, "", 1, 10),
    ]);

    private readonly MemoryRepository _repository = new();
    private readonly BusId _busId = BusId.New();

    private readonly RecordingBindings _bindings = new();

    private DeviceService CreateService() => new(_repository, Catalog, _bindings, new ModuleStatusStore(), NullLogger<DeviceService>.Instance);

    private Task<Result<IReadOnlyList<DeviceDescriptor>>> AddAsync(DriverId driver, string name, int start, int count)
        => CreateService().AddBatchAsync(new DeviceBatchRequest(_busId, driver, name, start, count), CancellationToken.None);

    // ---------- 批量添加 ----------

    [Fact]
    public async Task AddBatch_ExpandsConsecutiveAddresses_WithSuffixedNames()
    {
        var added = await AddAsync(Power, "电源", 3, 3);

        Assert.True(added.IsSuccess, added.ToString());
        Assert.Equal([3, 4, 5], added.Value.Select(d => d.Address));
        Assert.Equal(["电源-03", "电源-04", "电源-05"], added.Value.Select(d => d.Name));
        Assert.All(added.Value, d => Assert.Equal(Power, d.Driver));
        Assert.Equal(1, _repository.Writes);
    }

    [Fact]
    public async Task AddBatch_Single_KeepsNameAsIs()
    {
        var added = await AddAsync(Generic, "  温控表  ", 7, 1);

        Assert.Equal("温控表", Assert.Single(added.Value).Name);
    }

    [Fact]
    public async Task AddBatch_BeyondDriverRange_IsRejected_BeforeStorage()
    {
        var overflow = await AddAsync(Power, "电源", 9, 3);
        var badStart = await AddAsync(Power, "电源", 11, 1);
        var zeroCount = await AddAsync(Power, "电源", 1, 0);

        Assert.Equal(DeviceErrors.InvalidDescriptorCode, overflow.Error.Code);
        Assert.Contains("最多还能添加 2 个", overflow.Error.Message);
        Assert.Equal(DeviceErrors.InvalidDescriptorCode, badStart.Error.Code);
        Assert.Equal(DeviceErrors.InvalidDescriptorCode, zeroCount.Error.Code);
        Assert.Equal(0, _repository.Writes);
    }

    [Fact]
    public async Task AddBatch_ListsEveryConflictingAddress_AndWritesNothing()
    {
        Assert.True((await AddAsync(Generic, "已有", 2, 1)).IsSuccess);
        Assert.True((await AddAsync(Generic, "已有", 4, 1)).IsSuccess);

        var result = await AddAsync(Generic, "新", 1, 5);

        Assert.Equal(DeviceErrors.AddressConflictCode, result.Error.Code);
        Assert.Contains("2、4", result.Error.Message);
        Assert.Equal(2, _repository.Writes);
    }

    [Fact]
    public async Task AddBatch_UnknownDriver_IsRejected()
    {
        var result = await AddAsync(DriverId.Of("removed.driver"), "x", 1, 1);

        Assert.Equal(DeviceErrors.UnknownDriverCode, result.Error.Code);
        Assert.Equal(0, _repository.Writes);
    }

    [Fact]
    public async Task AddBatch_NameTooLongForSuffix_ReportsOnce()
    {
        var result = await AddAsync(Generic, new string('n', DeviceDescriptor.MaxNameLength - 1), 1, 3);

        Assert.Equal(DeviceErrors.InvalidDescriptorCode, result.Error.Code);
        Assert.Single(result.Error.Message.Split('；'));
    }

    // ---------- 修改 ----------

    [Fact]
    public async Task Update_OutsideDriverRange_IsRejected_BeforeStorage()
    {
        var device = new DeviceDescriptor(DeviceId.New(), _busId, Power, "电源", 11);

        var result = await CreateService().UpdateAsync(device, CancellationToken.None);

        Assert.Equal(DeviceErrors.DescriptorCodePrefix + "Address", result.Error.Code);
        Assert.Equal(0, _repository.Writes);
    }

    [Fact]
    public async Task Update_UnknownDriver_StillAllowed_WithinAbsoluteRange()
    {
        var device = new DeviceDescriptor(DeviceId.New(), _busId, DriverId.Of("removed.driver"), "旧模块", 200);

        Assert.True((await CreateService().UpdateAsync(device, CancellationToken.None)).IsSuccess);
    }

    // ---------- 驱动目录 ----------

    [Fact]
    public void BuiltInCatalog_IsValid_AndContainsGenericModule()
    {
        var catalog = new DriverCatalog();

        Assert.True(catalog.TryGet(DriverCatalog.GenericModule, out var generic));
        Assert.Equal(HardwareCategory.General, generic.Category);
    }

    [Fact]
    public void Catalog_RejectsDuplicateIds_AndInvalidRange()
    {
        Assert.Throws<InvalidOperationException>(() => new DriverCatalog(
        [
            new DriverDescriptor(Generic, "a", HardwareCategory.General, "", 1, 2),
            new DriverDescriptor(Generic, "b", HardwareCategory.General, "", 1, 2),
        ]));

        Assert.Throws<InvalidOperationException>(() => new DriverCatalog(
            [new DriverDescriptor(Generic, "a", HardwareCategory.General, "", 0, 256)]));
    }

    [Theory]
    [InlineData("generic.module", true)]
    [InlineData("power_supply-v2", true)]
    [InlineData("Upper.Case", false)]
    [InlineData("有中文", false)]
    [InlineData("", false)]
    public void DriverId_Format(string text, bool valid)
        => Assert.Equal(valid, DriverId.TryParse(text, out _));

    // ---------- 表单 ----------

    [Theory]
    [InlineData("", "1")]
    [InlineData("abc", "1")]
    [InlineData("1", "x")]
    public void BatchForm_NonIntegerInput_ShowsError(string start, string count)
    {
        var form = new ModuleBatchFormViewModel();
        form.SelectDriver(new DriverItemViewModel(Catalog.All[0]), 1);
        form.StartAddressText = start;
        form.CountText = count;

        Assert.True(form.BuildRequest(_busId).IsFailure);
        Assert.True(form.HasError);
        Assert.Equal("-", form.AddressPreview);
    }

    [Fact]
    public void BatchForm_SelectDriver_PrefillsName_AndPreviewsAddresses()
    {
        Catalog.TryGet(Power, out var power);
        var form = new ModuleBatchFormViewModel();

        form.SelectDriver(new DriverItemViewModel(power!), 3);
        form.CountText = "4";

        Assert.Equal("程控电源", form.Name);
        Assert.Equal("地址 3 – 6，共 4 个", form.AddressPreview);
        var request = form.BuildRequest(_busId).Value;
        Assert.Equal((Power, 3, 4), (request.Driver, request.StartAddress, request.Count));
    }

    [Fact]
    public void BatchForm_WithoutDriver_IsRejected()
    {
        var form = new ModuleBatchFormViewModel();

        Assert.True(form.BuildRequest(_busId).IsFailure);
        Assert.False(form.HasDriver);
    }

    [Fact]
    public void EditForm_KeepsIdAndBus_AndCanChangeDriver()
    {
        var drivers = Catalog.All.Select(d => new DriverItemViewModel(d)).ToList();
        var power = drivers.Single(d => d.Driver.Id == Power);
        var generic = drivers.Single(d => d.Driver.Id == Generic);
        var device = new DeviceDescriptor(DeviceId.New(), _busId, Power, "电源", 4) { Remark = "1 号柜" };
        var form = new DeviceEditViewModel();
        form.LoadFrom(device, drivers, power);
        form.Name = "  2 号电源 ";
        form.Remark = " ";
        form.SelectedDriver = generic;

        var built = form.BuildDescriptor().Value;

        Assert.Equal(device with { Driver = Generic, Name = "2 号电源", Remark = null }, built);
    }

    [Fact]
    public async Task Update_ToDriverWithNarrowerRange_ChecksNewRange()
    {
        var device = new DeviceDescriptor(DeviceId.New(), _busId, Generic, "模块", 200);

        var result = await CreateService().UpdateAsync(device with { Driver = Power }, CancellationToken.None);

        Assert.Equal(DeviceErrors.DescriptorCodePrefix + "Address", result.Error.Code);
    }

    // ---------- 删除 ----------

    [Fact]
    public async Task Delete_Many_InOneCall()
    {
        var added = (await AddAsync(Generic, "模块", 1, 5)).Value;

        var deleted = await CreateService().DeleteAsync([added[0], added[2], added[4]], CancellationToken.None);

        Assert.Equal(3, deleted.Value);
        Assert.Equal([2, 4], _repository.Devices.Select(d => d.Address));
    }

    [Fact]
    public async Task Delete_Empty_DoesNotTouchStorage()
    {
        Assert.Equal(0, (await CreateService().DeleteAsync([], CancellationToken.None)).Value);
        Assert.Equal(0, _repository.Writes);
    }
}
