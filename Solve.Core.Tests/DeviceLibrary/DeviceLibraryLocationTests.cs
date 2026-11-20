using Microsoft.Extensions.Logging.Abstractions;
using Solve.Core.Application.DeviceLibrary;
using Solve.Core.Application.Settings.Ports;
using Solve.Core.Domain.Common;
using Solve.Desktop.Composition;

namespace Solve.Core.Tests.DeviceLibrary;

/// <summary>
/// 驱动库目录：默认目录的推断、保存的目录在启动时的解析、设置页保存（重启后生效）
/// </summary>
public sealed class DeviceLibraryLocationTests : IDisposable
{
    private sealed class MemorySettings : IAppSettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool FailReads { get; set; }

        public Task<Result<string?>> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(FailReads
                ? Result<string?>.Failure(new Error("Settings.Storage.Failed", "读库失败"))
                : Result<string?>.Success(Values.GetValueOrDefault(key)));

        public Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.FromResult(Result.Success());
        }
    }

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();

    private string Default => Path.Combine(_workspace, "app", "DeviceLibrary");

    public DeviceLibraryLocationTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private Task<DeviceLibraryLocation> ResolveAsync()
        => DeviceLibraryLocation.ResolveAsync(_settings, Default, NullLogger.Instance, CancellationToken.None);

    // ---------- 默认目录 ----------

    [Fact]
    public void Default_FromSourceTree_IsDevicelibraryNextToSolution()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_workspace, "repo")).FullName;
        File.WriteAllText(Path.Combine(repo, DesktopOptions.SolutionFileName), "<Solution />");
        var library = Directory.CreateDirectory(Path.Combine(repo, DesktopOptions.SourceDeviceLibraryDirectoryName)).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(repo, "Solve.Desktop", "bin", "Debug", "net10.0")).FullName;

        Assert.Equal(library, DesktopOptions.DefaultDeviceLibraryDirectory(bin));
        Assert.Equal(library, DesktopOptions.FromArgs([], bin).DeviceLibraryDirectory);
    }

    [Fact]
    public void Default_WithoutSourceTree_IsUnderProgramDirectory()
    {
        var app = Directory.CreateDirectory(Path.Combine(_workspace, "deployed")).FullName;

        Assert.Equal(Path.Combine(app, DesktopOptions.DeviceLibraryDirectoryName), DesktopOptions.DefaultDeviceLibraryDirectory(app));
    }

    [Fact]
    public void Default_SolutionWithoutDevicelibrary_IsUnderProgramDirectory()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_workspace, "repo")).FullName;
        File.WriteAllText(Path.Combine(repo, DesktopOptions.SolutionFileName), "<Solution />");
        var bin = Directory.CreateDirectory(Path.Combine(repo, "bin")).FullName;

        Assert.Equal(Path.Combine(bin, DesktopOptions.DeviceLibraryDirectoryName), DesktopOptions.DefaultDeviceLibraryDirectory(bin));
    }

    // ---------- 启动时解析 ----------

    [Fact]
    public async Task NothingSaved_UsesDefault()
    {
        var location = await ResolveAsync();

        Assert.Equal((Default, Default, Default), (location.CurrentDirectory, location.DefaultDirectory, location.NextDirectory));
        Assert.Null(location.StartupFallbackReason);
    }

    [Fact]
    public async Task SavedDirectory_IsUsed()
    {
        var custom = Path.Combine(_workspace, "custom");
        _settings.Values[DeviceLibraryLocation.SettingKey] = custom;

        var location = await ResolveAsync();

        Assert.Equal(custom, location.CurrentDirectory);
        Assert.Null(location.StartupFallbackReason);
    }

    [Theory]
    [InlineData("relative\\library")]
    [InlineData("C:")]
    public async Task UnusableSavedDirectory_FallsBackToDefault_WithReason(string saved)
    {
        _settings.Values[DeviceLibraryLocation.SettingKey] = saved;

        var location = await ResolveAsync();

        Assert.Equal(Default, location.CurrentDirectory);
        Assert.Contains(saved, location.StartupFallbackReason);
    }

    [Fact]
    public async Task SavedPathPointingToFile_FallsBackToDefault()
    {
        var file = Path.Combine(_workspace, "not-a-directory.txt");
        File.WriteAllText(file, "x");
        _settings.Values[DeviceLibraryLocation.SettingKey] = file;

        Assert.Equal(Default, (await ResolveAsync()).CurrentDirectory);
    }

    [Fact]
    public async Task ReadFailure_FallsBackToDefault()
    {
        _settings.FailReads = true;

        var location = await ResolveAsync();

        Assert.Equal(Default, location.CurrentDirectory);
        Assert.NotNull(location.StartupFallbackReason);
    }

    // ---------- 保存 ----------

    [Fact]
    public async Task Save_CreatesDirectory_AndTakesEffectNextStart()
    {
        var location = await ResolveAsync();
        var custom = Path.Combine(_workspace, "custom", "Devicelibrary");

        var saved = await location.SaveAsync(custom + Path.DirectorySeparatorChar, CancellationToken.None);

        Assert.Equal(custom, saved.Value);
        Assert.True(Directory.Exists(custom));
        Assert.Equal((Default, custom), (location.CurrentDirectory, location.NextDirectory));
        Assert.Equal(custom, (await ResolveAsync()).CurrentDirectory);
    }

    [Fact]
    public async Task Save_Default_StoresEmpty_SoItFollowsTheDefault()
    {
        _settings.Values[DeviceLibraryLocation.SettingKey] = Path.Combine(_workspace, "custom");
        var location = await ResolveAsync();

        Assert.True((await location.SaveAsync(null, CancellationToken.None)).IsSuccess);

        Assert.Equal(string.Empty, _settings.Values[DeviceLibraryLocation.SettingKey]);
        Assert.Equal(Default, location.NextDirectory);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("x:relative")]
    public async Task Save_RelativePath_IsRefused(string input)
    {
        var location = await ResolveAsync();

        var result = await location.SaveAsync(input, CancellationToken.None);

        Assert.Equal(DeviceLibraryLocationErrors.InvalidPathCode, result.Error.Code);
        Assert.False(_settings.Values.ContainsKey(DeviceLibraryLocation.SettingKey));
    }
}
