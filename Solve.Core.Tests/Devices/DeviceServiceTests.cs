using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.Devices;
using Solve.Core.Application.Devices.Ports;
using Solve.Core.Domain.Buses;
using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.UI.ViewModels;

namespace Solve.Core.Tests.Devices;

public sealed class DeviceServiceTests
{
    /// <summary>
    /// 只记录调用次数的仓储：验证无效输入不会触达存储
    /// </summary>
    private sealed class CountingRepository : IDeviceRepository
    {
        public int Writes { get; private set; }

        public Task<Result<IReadOnlyList<DeviceDescriptor>>> ListByBusAsync(BusId busId, CancellationToken cancellationToken)
            => Task.FromResult(Result<IReadOnlyList<DeviceDescriptor>>.Success([]));

        public Task<Result<int>> CountByBusAsync(BusId busId, CancellationToken cancellationToken)
            => Task.FromResult(Result<int>.Success(0));

        public Task<Result> AddAsync(DeviceDescriptor device, CancellationToken cancellationToken) => Write();

        public Task<Result> UpdateAsync(DeviceDescriptor device, CancellationToken cancellationToken) => Write();

        public Task<Result> DeleteAsync(DeviceId id, CancellationToken cancellationToken) => Write();

        private Task<Result> Write()
        {
            Writes++;
            return Task.FromResult(Result.Success());
        }
    }

    private readonly CountingRepository _repository = new();

    private DeviceService CreateService() => new(_repository, NullLogger<DeviceService>.Instance);

    [Fact]
    public async Task InvalidDevice_IsRejected_BeforeStorage()
    {
        var invalid = new DeviceDescriptor(DeviceId.New(), BusId.New(), "", 0);

        var added = await CreateService().AddAsync(invalid, CancellationToken.None);
        var updated = await CreateService().UpdateAsync(invalid, CancellationToken.None);

        Assert.Equal(DeviceErrors.InvalidDescriptorCode, added.Error.Code);
        Assert.Equal(DeviceErrors.InvalidDescriptorCode, updated.Error.Code);
        Assert.Equal(0, _repository.Writes);
    }

    [Fact]
    public async Task ValidDevice_IsSaved()
    {
        var device = new DeviceDescriptor(DeviceId.New(), BusId.New(), "程控电源", 1);

        Assert.True((await CreateService().AddAsync(device, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, _repository.Writes);
    }

    // ---------- 编辑表单 ----------

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void EditForm_NonIntegerAddress_ShowsError(string address)
    {
        var form = new DeviceEditViewModel();
        form.ResetForCreate(1);
        form.AddressText = address;

        var built = form.BuildDescriptor(BusId.New());

        Assert.True(built.IsFailure);
        Assert.Equal(DeviceErrors.DescriptorCodePrefix + "Address", built.Error.Code);
        Assert.True(form.HasError);
    }

    [Fact]
    public void EditForm_TrimsInput_AndBlankRemarkIsNull()
    {
        var busId = BusId.New();
        var form = new DeviceEditViewModel();
        form.ResetForCreate(3);
        form.Name = "  温控表  ";
        form.Remark = "   ";

        var built = form.BuildDescriptor(busId).Value;

        Assert.Equal("温控表", built.Name);
        Assert.Equal(3, built.Address);
        Assert.Equal(busId, built.BusId);
        Assert.Null(built.Remark);
        Assert.False(form.HasError);
    }

    [Fact]
    public void EditForm_Edit_KeepsDeviceId()
    {
        var device = new DeviceDescriptor(DeviceId.New(), BusId.New(), "程控电源", 4) { Remark = "1 号柜" };
        var form = new DeviceEditViewModel();
        form.LoadFrom(device);

        Assert.False(form.IsCreateMode);
        Assert.Equal(device, form.BuildDescriptor(device.BusId).Value);
    }
}
