using Solve.Core.Application.Logging;
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

        var deleted = DailyLogFiles.SelectForDeletion(files, Today, retentionDays: 30, protectedPath: null);

        Assert.Equal(["2026_10_19.log"], deleted.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void SelectForDeletion_IgnoresFileSize_OnlyDaysCount()
    {
        // 保存期内的日志再大也不删：删除只有保存天数这一个条件
        const long huge = 50L * 1024 * 1024 * 1024;
        var files = new[]
        {
            File_("2026_11_16.log", new DateOnly(2026, 11, 16), length: huge),
            File_("2026_11_17.log", new DateOnly(2026, 11, 17), length: huge),
            File_("2026_11_18.log", Today, length: huge),
        };

        Assert.Empty(DailyLogFiles.SelectForDeletion(files, Today, retentionDays: 3, protectedPath: null));
    }

    [Fact]
    public void SelectForDeletion_NeverDeletesTheFileBeingWritten()
    {
        // 跨午夜时前一天的文件可能仍在写入（午夜前产生、午夜后出队的日志）
        var active = File_("2026_11_17.log", new DateOnly(2026, 11, 17));
        var files = new[] { File_("2026_11_16.log", new DateOnly(2026, 11, 16)), active, File_("2026_11_18.log", Today) };

        var deleted = DailyLogFiles.SelectForDeletion(files, Today, retentionDays: 1, protectedPath: active.Path);

        Assert.Equal(["2026_11_16.log"], deleted.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void Prune_DeletesOnlyExpiredLogFiles()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var expired = Touch(DailyLogFiles.FileName(today.AddDays(-40)));
        var expiredLegacy = Touch($"solve-{today.AddDays(-35):yyyyMMdd}.log");
        var kept = Touch(DailyLogFiles.FileName(today.AddDays(-29)));
        var unrelated = Touch("notes.log");

        var deleted = DailyLogFiles.Prune(_directory, today, retentionDays: 30, protectedPath: null);

        // 按日期从旧到新：40 天前的在 35 天前的旧命名文件之前
        Assert.Equal([expired, expiredLegacy], deleted.Select(f => f.Path));
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(expiredLegacy));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Prune_UsesTheGivenRetentionDays()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var eightDaysAgo = Touch(DailyLogFiles.FileName(today.AddDays(-8)));
        var sixDaysAgo = Touch(DailyLogFiles.FileName(today.AddDays(-6)));

        var deleted = DailyLogFiles.Prune(_directory, today, retentionDays: 7, protectedPath: null);

        Assert.Equal([eightDaysAgo], deleted.Select(f => f.Path));
        Assert.True(File.Exists(sixDaysAgo));
    }

    [Fact]
    public void Prune_MissingDirectory_DeletesNothing()
    {
        Assert.Empty(DailyLogFiles.Prune(Path.Combine(_directory, "absent"), Today, retentionDays: 1, protectedPath: null));
    }

    // ==================== 分卷序号与汇总 ====================

    [Theory]
    [InlineData("2026_11_18_999.log", 999)]
    [InlineData("2026_11_18_1000.log", 1000)]
    [InlineData("2026_11_18_123456789.log", 123456789)]
    [InlineData("solve-20261118_1000.log", 1000)]
    public void TryParse_RecognizesFourDigitAndLongerSequences(string name, int sequence)
    {
        // 一天超过 999 卷时 Serilog 写出 4 位序号：不识别就既不能查看、也不会被清理
        Assert.True(DailyLogFiles.TryParse(name, 10, out var file));
        Assert.Equal(sequence, file.Sequence);
    }

    [Theory]
    [InlineData("2026_11_18_01.log")]
    [InlineData("2026_11_18_1234567890.log")]
    public void TryParse_RejectsTooShortOrTooLongSequences(string name)
    {
        Assert.False(DailyLogFiles.TryParse(name, 10, out _));
    }

    [Fact]
    public void List_OrdersFourDigitSequencesAfterThreeDigitOnes()
    {
        Touch("2026_11_18_1000.log");
        Touch("2026_11_18_999.log");
        Touch("2026_11_18.log");

        var names = DailyLogFiles.List(_directory).Select(f => Path.GetFileName(f.Path));

        Assert.Equal(["2026_11_18.log", "2026_11_18_999.log", "2026_11_18_1000.log"], names);
    }

    [Fact]
    public void Summarize_CountsBytesAndDateRange()
    {
        var files = new[]
        {
            File_("2026_11_16.log", new DateOnly(2026, 11, 16), length: 100),
            File_("2026_11_16_001.log", new DateOnly(2026, 11, 16), length: 50, sequence: 1),
            File_("2026_11_14.log", new DateOnly(2026, 11, 14), length: 10),
        };

        Assert.Equal(new LogPrunePreview(3, 160, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 16)), DailyLogFiles.Summarize(files));
        Assert.Equal(LogPrunePreview.None, DailyLogFiles.Summarize([]));
    }
}
