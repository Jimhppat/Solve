using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging;

/// <summary>
/// 按日期命名的日志文件约定：命名、识别、排序、保留期选择。选择逻辑是纯函数，不碰磁盘。
/// </summary>
public sealed class DailyLogFilesTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Touch(string name, int bytes = 1)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static DailyLogFile File_(string name, DateOnly date, long length = 100, int sequence = 0)
        => new(Path.Combine("C:\\logs", name), date, sequence, IsLegacy: false, length);

    // ==================== 命名与识别 ====================

    [Fact]
    public void FileName_IsYearMonthDay()
    {
        Assert.Equal("2026_11_18.log", DailyLogFiles.FileName(Today));
        Assert.Equal("2027_01_05.log", DailyLogFiles.FileName(new DateOnly(2027, 1, 5)));
    }

    [Theory]
    [InlineData("2026_11_18.log", 2026, 11, 18, 0, false)]
    [InlineData("2026_11_18_002.log", 2026, 11, 18, 2, false)]
    [InlineData("solve-20261117.log", 2026, 11, 17, 0, true)]
    [InlineData("solve-20261117_001.log", 2026, 11, 17, 1, true)]
    public void TryParse_RecognizesCurrentAndLegacyNames(string name, int year, int month, int day, int sequence, bool legacy)
    {
        Assert.True(DailyLogFiles.TryParse(name, 10, out var file));
        Assert.Equal(new DateOnly(year, month, day), file.Date);
        Assert.Equal(sequence, file.Sequence);
        Assert.Equal(legacy, file.IsLegacy);
    }

    [Theory]
    [InlineData("2026_13_01.log")]
    [InlineData("2026_02_30.log")]
    [InlineData("2026-11-18.log")]
    [InlineData("11_18_2026.log")]
    [InlineData("2026_11_18.txt")]
    [InlineData("serilog-selflog.txt")]
    [InlineData("notes.log")]
    public void TryParse_RejectsOtherFiles(string name)
    {
        Assert.False(DailyLogFiles.TryParse(name, 10, out _));
    }

    [Fact]
    public void List_OrdersByDate_LegacyFirst_ThenSequence_AndSkipsOtherFiles()
    {
        Touch("2026_11_18_001.log");
        Touch("2026_11_18.log");
        Touch("solve-20261118.log");
        Touch("2026_11_17.log");
        Touch("notes.log");
        Touch("serilog-selflog.txt");

        var names = DailyLogFiles.List(_directory).Select(f => Path.GetFileName(f.Path));

        Assert.Equal(["2026_11_17.log", "solve-20261118.log", "2026_11_18.log", "2026_11_18_001.log"], names);
    }

    [Fact]
    public void List_MissingDirectory_IsEmpty()
    {
        Assert.Empty(DailyLogFiles.List(Path.Combine(_directory, "absent")));
    }

    // ==================== 保留期 ====================

    [Fact]
    public void SelectForDeletion_KeepsExactlyRetentionDaysIncludingToday()
    {
        var files = new[]
        {
            File_("2026_10_19.log", new DateOnly(2026, 10, 19)), // 第 31 天前：删
            File_("2026_10_20.log", new DateOnly(2026, 10, 20)), // 含今天共 30 天：留
            File_("2026_11_18.log", Today),
        };

        var deleted = DailyLogFiles.SelectForDeletion(files, Today, retentionDays: 30, maxTotalBytes: long.MaxValue, protectedPath: null);

        Assert.Equal(["2026_10_19.log"], deleted.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void SelectForDeletion_OverSizeCap_DeletesOldestFirst()
    {
        var files = new[]
        {
            File_("2026_11_16.log", new DateOnly(2026, 11, 16)),
            File_("2026_11_17.log", new DateOnly(2026, 11, 17)),
            File_("2026_11_18.log", Today),
        };

        var deleted = DailyLogFiles.SelectForDeletion(files, Today, 30, maxTotalBytes: 250, protectedPath: null);

        Assert.Equal(["2026_11_16.log"], deleted.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void SelectForDeletion_NeverDeletesTheFileBeingWritten()
    {
        var active = File_("2026_11_16.log", new DateOnly(2026, 11, 16));
        var files = new[] { active, File_("2026_11_17.log", new DateOnly(2026, 11, 17)), File_("2026_11_18.log", Today) };

        var bySize = DailyLogFiles.SelectForDeletion(files, Today, 30, maxTotalBytes: 250, protectedPath: active.Path);
        var byAge = DailyLogFiles.SelectForDeletion(files, Today, retentionDays: 1, long.MaxValue, protectedPath: active.Path);

        Assert.Equal(["2026_11_17.log"], bySize.Select(f => Path.GetFileName(f.Path)));
        Assert.DoesNotContain(byAge, f => f.Path == active.Path);
    }

    [Fact]
    public void Prune_DeletesOnlyExpiredLogFiles()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var expired = Touch(DailyLogFiles.FileName(today.AddDays(-40)));
        var expiredLegacy = Touch($"solve-{today.AddDays(-35):yyyyMMdd}.log");
        var kept = Touch(DailyLogFiles.FileName(today.AddDays(-29)));
        var unrelated = Touch("notes.log");

        var deleted = DailyLogFiles.Prune(_directory, today, protectedPath: null);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(expiredLegacy));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(unrelated));
    }
}
