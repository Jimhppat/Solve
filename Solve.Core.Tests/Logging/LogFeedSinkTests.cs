using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Solve.Core.Application.Common;
using Solve.Core.Application.Logging;
using Solve.Core.Tests.Logging.TestSupport;
using Solve.Desktop.Logging;

namespace Solve.Core.Tests.Logging;

public sealed class LogFeedSinkTests
{
    private const string Source = "Solve.Core.Application.Buses.BusRuntime";

    // 同步写入：不经异步队列，断言时事件已经推送
    private static (Logger Logger, LogFeedSink Sink) Create(int recentCapacity = LogFeedSink.DefaultRecentCapacity)
    {
        var sink = new LogFeedSink(recentCapacity);
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    private static Serilog.ILogger From(Logger logger, string source)
        => logger.ForContext(Constants.SourceContextPropertyName, source);

    private static List<LogEntry> Drain(EventSubscription<LogEntry> subscription)
    {
        var items = new List<LogEntry>();
        while (subscription.Reader.TryRead(out var item))
            items.Add(item);
        return items;
    }

    [Fact]
    public void Emit_MapsLevelSourceMessageAndException()
    {
        var (logger, sink) = Create();
        var failure = new InvalidOperationException("boom");

        From(logger, Source).Error(failure, "总线 {Name} 模式={Mode}", "PLC-01", DayOfWeek.Monday);
        sink.Subscribe(out var recent).Dispose();

        var entry = Assert.Single(recent);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(Source, entry.Source);
        // 与日志文件一致：字符串不加引号
        Assert.Equal("总线 PLC-01 模式=Monday", entry.Message);
        Assert.Contains("boom", entry.Exception);
    }

    [Theory]
    [InlineData(Serilog.Events.LogEventLevel.Verbose, LogLevel.Trace)]
    [InlineData(Serilog.Events.LogEventLevel.Debug, LogLevel.Debug)]
    [InlineData(Serilog.Events.LogEventLevel.Information, LogLevel.Information)]
    [InlineData(Serilog.Events.LogEventLevel.Warning, LogLevel.Warning)]
    [InlineData(Serilog.Events.LogEventLevel.Error, LogLevel.Error)]
    [InlineData(Serilog.Events.LogEventLevel.Fatal, LogLevel.Critical)]
    public void Emit_MapsEverySerilogLevel(Serilog.Events.LogEventLevel serilogLevel, LogLevel expected)
    {
        var (logger, sink) = Create();

        From(logger, Source).Write(serilogLevel, "msg");
        sink.Subscribe(out var recent).Dispose();

        Assert.Equal(expected, Assert.Single(recent).Level);
    }

    [Fact]
    public void Emit_WithoutSourceContext_UsesEmptySource()
    {
        var (logger, sink) = Create();

        logger.Information("no source");
        sink.Subscribe(out var recent).Dispose();

        Assert.Equal(string.Empty, Assert.Single(recent).Source);
    }

    [Fact]
    public void AvaloniaSources_AreNotPushed_ButLookalikesAre()
    {
        var (logger, sink) = Create();

        From(logger, "Avalonia.Binding").Warning("binding");
        From(logger, "Avalonia").Warning("root");
        From(logger, "AvaloniaExtras.Charts").Warning("lookalike");
        sink.Subscribe(out var recent).Dispose();

        Assert.Equal(["lookalike"], recent.Select(e => e.Message));
    }

    [Fact]
    public void Recent_KeepsNewestEntriesInOrder()
    {
        var (logger, sink) = Create(recentCapacity: 3);

        for (var i = 1; i <= 5; i++)
            From(logger, Source).Information("m{N}", i);
        sink.Subscribe(out var recent).Dispose();

        Assert.Equal(["m3", "m4", "m5"], recent.Select(e => e.Message));
    }

    [Fact]
    public void SnapshotAndSubscription_JoinWithoutGapOrDuplicate()
    {
        var (logger, sink) = Create();
        From(logger, Source).Information("before-1");
        From(logger, Source).Information("before-2");

        using var subscription = sink.Subscribe(out var recent);
        From(logger, Source).Information("after");

        Assert.Equal(["before-1", "before-2"], recent.Select(e => e.Message));
        Assert.Equal(["after"], Drain(subscription).Select(e => e.Message));
    }

    [Fact]
    public async Task Complete_EndsSubscriptions()
    {
        var (_, sink) = Create();
        using var subscription = sink.Subscribe(out _);

        sink.Complete();

        Assert.False(await subscription.Reader.WaitToReadAsync());
    }
}

/// <summary>
/// 经完整日志管线（异步队列）推送到界面的路径
/// </summary>
[Collection(LoggingPipelineCollection.Name)]
public sealed class LoggingPipelineFeedTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solve-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task PipelineFeed_PushesEntries_AndCompletesOnDispose()
    {
        var pipeline = LoggingPipeline.Create(_directory, settingsPath: null);
        using var subscription = pipeline.Feed.Subscribe(out _);

        pipeline.Logger.ForContext(Constants.SourceContextPropertyName, "Solve.Test").Information("to-panel");
        pipeline.Dispose();

        var received = new List<LogEntry>();
        while (await subscription.Reader.WaitToReadAsync())
        {
            while (subscription.Reader.TryRead(out var entry))
                received.Add(entry);
        }

        Assert.Contains(received, e => e.Message == "to-panel" && e.Source == "Solve.Test");
    }
}
